import { html, nothing } from 'lit';
import { repeat } from 'lit/directives/repeat.js';
import type { ServiceBlueprintStageValidationRule, StageDefinition } from '../types.js';
import { collectStageInputFields } from '../component-property-references.js';
import { tryParseExpression } from '../calculation-runtime.js';
import '../wayfinder-calculation-expression-editor.js';
import { validationEdits } from './calculation-edits.js';
import { type CalculationsContext, completionsFor } from './calculations-context.js';

type ExpressionEvent = CustomEvent<{ value: string }>;

/**
 * The "Validations" section. Unlike fields/tables/series (one blueprint-wide `calculations`
 * block), a stage's `validations` are per-stage — see ServiceBlueprintStageValidationRule.cs and
 * docs/guides/calculation-language.md's "Stage validations". `when`/`rule` are ordinary expressions
 * in the same language, evaluated against the identical blueprint-wide scope, so a rule may freely
 * reference a field captured on an earlier stage.
 */
export class ValidationsSection {
  constructor(private readonly _context: CalculationsContext) {}

  private _setRules(stage: StageDefinition, rules: ServiceBlueprintStageValidationRule[]) {
    this._context.updateStage(stage.stageKey, { validations: rules });
  }

  private _add(stage: StageDefinition) {
    this._setRules(stage, validationEdits.add(stage));
    this._context.announce(`Validation rule added to ${stage.displayName}.`);
  }

  private _delete(stage: StageDefinition, index: number) {
    this._setRules(stage, validationEdits.remove(stage, index));
    this._context.announce(`Validation rule removed from ${stage.displayName}.`);
  }

  private _patch(stage: StageDefinition, index: number, patch: Partial<ServiceBlueprintStageValidationRule>) {
    this._setRules(stage, validationEdits.patch(stage, index, patch));
  }

  render() {
    const { stages } = this._context;
    const totalRules = stages.reduce((sum, stage) => sum + (stage.validations ?? []).length, 0);

    return html`
      <details class="calc-section">
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Validations</h3>
          <span class="calc-section-meta">${totalRules}</span>
        </summary>
        <p class="calc-section-hint">
          Cross-field business rules checked before a stage can advance — the declarative
          alternative to a host writing custom validation code. A rule may reference any input or
          calculated field, including one captured on an earlier stage.
        </p>

        ${stages.map(
          (stage) => html`
            <div class="calc-validations-stage">
              <h4 class="calc-validations-stage-title">${stage.displayName} <span class="calc-section-meta">(${stage.stageKey})</span></h4>
              <ul class="calc-field-list">
                ${repeat(
                  stage.validations ?? [],
                  (_, index) => `${stage.stageKey}-${index}`,
                  (rule, index) => this._renderRule(stage, rule, index)
                )}
              </ul>
              <button type="button" class="secondary-button" @click=${() => this._add(stage)}>+ Add validation rule</button>
            </div>
          `
        )}
      </details>
    `;
  }

  /**
   * Same parse check stageValidationRuleIssues() (service-blueprint-validation.ts) blocks Save with
   * — shown here too so the error is visible right where it was typed, not just in the Validation
   * tab, matching how a calc field's own parse error already surfaces inline.
   */
  private _parseError(parse: ReturnType<typeof tryParseExpression> | null) {
    return parse && !parse.ok
      ? html`<span class="calc-preview calc-preview-error" data-wayfinder-calc-validation-preview>${parse.message}</span>`
      : nothing;
  }

  private _expression(
    label: string,
    stage: StageDefinition,
    rule: ServiceBlueprintStageValidationRule,
    index: number,
    which: 'when' | 'rule',
    completions: ReturnType<typeof completionsFor>
  ) {
    const value = which === 'when' ? (rule.when ?? '') : rule.rule;
    const parse = which === 'when' ? (rule.when?.trim() ? tryParseExpression(rule.when) : null) : tryParseExpression(rule.rule);
    return html`
      <div class="field-block calc-expression-block">
        <span class="field-label">${label}</span>
        <wayfinder-calculation-expression-editor
          .value=${value}
          .completions=${completions}
          label-text="${rule.code || 'validation'} ${which}"
          @expression-input=${(event: ExpressionEvent) =>
            this._patch(stage, index, which === 'when' ? { when: event.detail.value || undefined } : { rule: event.detail.value })}
        ></wayfinder-calculation-expression-editor>
        ${this._parseError(parse)}
      </div>
    `;
  }

  private _renderRule(stage: StageDefinition, rule: ServiceBlueprintStageValidationRule, index: number) {
    const stageFields = collectStageInputFields(stage.components, this._context.componentCatalog);
    const completions = completionsFor(this._context, { names: Object.keys(this._context.calculations.fields), detail: 'field' });

    return html`
      <li class="calc-field-row" data-wayfinder-calc-validation=${`${stage.stageKey}-${index}`}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Code</span>
            <input
              class="field-control"
              .value=${rule.code}
              @change=${(event: Event) => this._patch(stage, index, { code: (event.currentTarget as HTMLInputElement).value })}
            />
          </label>

          <label class="field-block">
            <span class="field-label">Attach to field (optional)</span>
            <select
              class="field-control"
              @change=${(event: Event) => this._patch(stage, index, { field: (event.currentTarget as HTMLSelectElement).value || undefined })}
            >
              <option value="" ?selected=${!rule.field}>-- Stage-level (no field) --</option>
              ${stageFields.map(
                (field) =>
                  html`<option value=${field.fieldKey} ?selected=${field.fieldKey === rule.field}>${field.label} (${field.fieldKey})</option>`
              )}
            </select>
          </label>

          <button
            type="button"
            class="icon-button danger-button"
            aria-label="Delete validation rule ${rule.code || index}"
            @click=${() => this._delete(stage, index)}
          >Delete</button>
        </div>

        <div class="calc-field-row-body">
          ${this._expression('When (optional guard — skips this rule entirely if false)', stage, rule, index, 'when', completions)}
          ${this._expression('Rule (must evaluate to true)', stage, rule, index, 'rule', completions)}

          <label class="field-block">
            <span class="field-label">Message</span>
            <textarea
              class="field-control"
              rows="2"
              .value=${rule.message}
              @change=${(event: Event) => this._patch(stage, index, { message: (event.currentTarget as HTMLTextAreaElement).value })}
            ></textarea>
          </label>
        </div>
      </li>
    `;
  }
}
