import { LitElement, html, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ServiceBlueprint, StageDefinition, ComponentDescriptor, ServiceBlueprintCalculationSet } from './types.js';
import { collectStageInputFields, type FieldReference } from './component-property-references.js';
import { inScopeInputFieldKeys } from './calculation-runtime.js';
import { computeCalculationDiagnostics } from './calculation-diagnostics.js';
import { FieldsSection } from './calculations/fields-section.js';
import { SeriesSection } from './calculations/series-section.js';
import { TablesSection } from './calculations/tables-section.js';
import { ValidationsSection } from './calculations/validations-section.js';
import type { CalculationsContext } from './calculations/calculations-context.js';
import calculationsEditorStyles from './wayfinder-calculations-editor.css?inline';

const NUMERIC_INPUT_TYPES = new Set(['slider', 'number', 'decimal']);

/**
 * Visual authoring for `serviceBlueprint.calculations` (fields/tables/series — see
 * docs/guides/calculation-language.md) — the new "Calculations" tab. Writes the exact same JSON
 * shape MCP-driven agents already produce; there is no separate model here. See the
 * calculations-tab plan (project_reference_aware_property_fields session) for the design
 * rationale — most notably: field declaration order is fully automatic
 * (calculation-ordering.ts), never asked of the designer, and every reference-shaped value gets
 * an "insert a reference" affordance instead of requiring exact free-text spelling.
 *
 * Same `service-blueprint-updated` CustomEvent contract every other tab already uses
 * (wayfinder-step-inspector.ts, wayfinder-definition-editor.ts).
 */
@customElement('wayfinder-calculations-editor')
export class WayfinderCalculationsEditorElement extends LitElement implements CalculationsContext {
  @property({ attribute: false })
  serviceBlueprint: ServiceBlueprint | null = null;

  @property({ attribute: false })
  componentCatalog: ComponentDescriptor[] = [];

  @state() private _statusMessage: string | null = null;

  private readonly _fields = new FieldsSection(this);
  private readonly _tables = new TablesSection(this);
  private readonly _series = new SeriesSection(this);
  private readonly _validations = new ValidationsSection(this);

  get calculations(): ServiceBlueprintCalculationSet {
    return this.serviceBlueprint?.calculations ?? { fields: {} };
  }

  get stages(): StageDefinition[] {
    return this.serviceBlueprint?.stages ?? [];
  }

  get inputFields(): FieldReference[] {
    const allComponents = this.stages.flatMap((stage) => stage.components ?? []);
    return collectStageInputFields(allComponents, this.componentCatalog);
  }

  /** Every rule CalculationEvaluator.cs/CalculationScopeBuilder.cs would genuinely reject at
   * Save time, computed once here and reused wherever this tab needs to say something's wrong —
   * see calculation-diagnostics.ts, shared with the Definition tab's lint and the Validation tab
   * so none of the three re-derives its own subset of the same rules. Not every diagnostic kind
   * this can produce is rendered here: expression parse errors already show live via the
   * CodeMirror linter (calculation-expression-editor-codemirror.ts), and a series loop-variable
   * collision already shows live via tryEvaluateSeriesForPreview's own scope-based check — this
   * getter is only consulted for field-name collisions and the fields cycle banner, which have no
   * other live mechanism. */
  get diagnostics() {
    return computeCalculationDiagnostics({
      fields: this.calculations.fields,
      series: this.calculations.series ?? {},
      tableNames: new Set(Object.keys(this.calculations.tables ?? {})),
      inScopeInputFieldKeys: inScopeInputFieldKeys(this.inputFields),
    });
  }

  /** Every input's own declared default, coerced to the type the calculation scope expects —
   * mirrors CalculationScopeBuilder.cs's own coercion (numeric field types vs everything else)
   * exactly, so the live preview here matches what validate_service_blueprint's own static
   * check would see. A numeric field with no default is left out of the sample scope entirely
   * (no safe placeholder for a missing amount); every other field type gets one regardless of
   * whether a default was declared — an unfilled text box already means "" and an unticked
   * checkbox already means false everywhere else in this system — see inScopeInputFieldKeys in
   * calculation-runtime.ts for the same distinction applied to diagnostics. */
  get sampleInputs(): Record<string, unknown> {
    const inputs: Record<string, unknown> = {};
    for (const field of this.inputFields) {
      if (NUMERIC_INPUT_TYPES.has(field.type)) {
        if (field.default === undefined) {
          continue;
        }
        const numeric = Number(field.default);
        if (!Number.isNaN(numeric)) {
          inputs[field.fieldKey] = numeric;
        }
      } else if (field.type === 'boolean') {
        inputs[field.fieldKey] = field.default === 'true';
      } else {
        inputs[field.fieldKey] = field.default ?? '';
      }
    }
    return inputs;
  }

  updateCalculations(next: ServiceBlueprintCalculationSet) {
    if (this.serviceBlueprint) {
      this._emitUpdated({ ...this.serviceBlueprint, calculations: next });
    }
  }

  updateStage(stageKey: string, patch: Partial<StageDefinition>) {
    if (this.serviceBlueprint) {
      const stages = this.serviceBlueprint.stages.map((stage) => (stage.stageKey === stageKey ? { ...stage, ...patch } : stage));
      this._emitUpdated({ ...this.serviceBlueprint, stages });
    }
  }

  announce(message: string) {
    this._statusMessage = message;
  }

  private _emitUpdated(next: ServiceBlueprint) {
    this.dispatchEvent(new CustomEvent('service-blueprint-updated', { detail: { serviceBlueprint: next }, bubbles: true, composed: true }));
  }

  render() {
    if (!this.serviceBlueprint) {
      return html`<div class="calc-empty">No service blueprint loaded.</div>`;
    }

    return html`
      <div class="calc-root" data-wayfinder-component="calculations-editor">
        <div id="calc-announcer" class="sr-only" role="status" aria-live="polite" aria-atomic="true">${this._statusMessage ?? ''}</div>
        ${this._fields.render()}
        ${this._tables.render()}
        ${this._series.render()}
        ${this._validations.render()}
      </div>
    `;
  }

  static styles = unsafeCSS(calculationsEditorStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-calculations-editor': WayfinderCalculationsEditorElement;
  }
}
