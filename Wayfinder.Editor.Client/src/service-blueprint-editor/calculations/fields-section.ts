import { html, nothing } from 'lit';
import { repeat } from 'lit/directives/repeat.js';
import { tryEvaluateFieldsForPreview } from '../calculation-runtime.js';
import '../wayfinder-calculation-expression-editor.js';
import {
  type CalcFieldDefinition,
  type CalcFields,
  changedName,
  fieldEdits,
  fieldNameError,
  inDependencyOrder,
  omitKey,
  renameKey,
} from './calculation-edits.js';
import { type CalculationsContext, completionsFor } from './calculations-context.js';

type FieldPreview = ReturnType<typeof tryEvaluateFieldsForPreview>['results'][string] | undefined;

/** The "Fields" section: named values, each an expression or supplied by the host. Declaration order is automatic. */
export class FieldsSection {
  constructor(private readonly _context: CalculationsContext) {}

  /** The one place a `fields` edit becomes a real update: it always recomputes the dependency order and says what moved. */
  private _apply(fields: CalcFields, currentOrder: string[]) {
    const ordered = inDependencyOrder(fields, currentOrder);
    if (ordered.announcement) {
      this._context.announce(ordered.announcement);
    }
    this._context.updateCalculations({ ...this._context.calculations, fields: ordered.fields });
  }

  private get _fields() {
    return this._context.calculations.fields;
  }

  private _add(order: string[]) {
    const { fields, name } = fieldEdits.add(this._fields);
    this._apply(fields, order);
    this._context.announce(`${name} added.`);
  }

  private _delete(name: string, order: string[]) {
    this._apply(
      omitKey(this._fields, name),
      order.filter((existing) => existing !== name)
    );
    this._context.announce(`${name} deleted.`);
  }

  private _rename(oldName: string, typed: string, order: string[]) {
    const newName = changedName(oldName, typed);
    if (newName) {
      this._apply(
        renameKey(this._fields, oldName, newName),
        order.map((existing) => (existing === oldName ? newName : existing))
      );
    }
  }

  render() {
    const fields = this._fields;
    const order = Object.keys(fields);
    const { calculations, diagnostics, sampleInputs } = this._context;
    const preview = tryEvaluateFieldsForPreview(calculations, sampleInputs);
    const collidingFieldNames = new Set(diagnostics.filter((d) => d.kind === 'field-name-collision').map((d) => d.field));
    const cycle = diagnostics.find((d) => d.kind === 'field-cycle');
    const tableNames = Object.keys(calculations.tables ?? {});

    return html`
      <details class="calc-section" open>
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Fields</h3>
          <span class="calc-section-meta">${order.length}</span>
        </summary>

        ${
          cycle
            ? html`
              <div class="calc-cycle-banner" role="alert">
                Circular dependency: ${cycle.fields.join(' → ')} → ${cycle.fields[0]}.
                These fields reference each other in a loop and can never be ordered — fix one of
                these expressions before saving.
              </div>
            `
            : nothing
        }

        <ul class="calc-field-list">
          ${repeat(
            order,
            (name) => name,
            (name, index) => this._renderRow(name, fields[name], order, index, preview.results[name], collidingFieldNames, tableNames)
          )}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._add(order)}>+ Add field</button>
      </details>
    `;
  }

  private _renderRow(
    name: string,
    field: CalcFieldDefinition,
    order: string[],
    index: number,
    result: FieldPreview,
    collidingFieldNames: Set<string>,
    tableNames: string[]
  ) {
    const isService = (field.source ?? '').toLowerCase() === 'service';
    const nameError = fieldNameError(name, order, collidingFieldNames);
    // A field's own expression can only see the fields declared before it.
    const completions = completionsFor(
      this._context,
      { names: order.slice(0, index), detail: 'field' },
      { names: tableNames, detail: 'table' }
    );
    const edit = (change: (fields: CalcFields) => CalcFields) => this._apply(change(this._fields), order);

    return html`
      <li class="calc-field-row" data-wayfinder-calc-field=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input
              class="field-control ${nameError ? 'field-control-error' : ''}"
              .value=${name}
              @change=${(event: Event) => this._rename(name, (event.currentTarget as HTMLInputElement).value, order)}
            />
            ${nameError ? html`<span class="field-error">${nameError}</span>` : nothing}
          </label>

          <label class="field-toggle">
            <input
              type="checkbox"
              .checked=${isService}
              @change=${(event: Event) => edit((fields) => fieldEdits.setSource(fields, name, (event.currentTarget as HTMLInputElement).checked))}
            />
            <span>Supplied by the host (source: service)</span>
          </label>

          <button
            type="button"
            class="icon-button danger-button"
            aria-label="Delete field ${name}"
            @click=${() => this._delete(name, order)}
          >Delete</button>
        </div>

        ${isService ? this._renderServiceNote() : this._renderBody(name, field, completions, result, edit)}
      </li>
    `;
  }

  private _renderServiceNote() {
    return html`<p class="calc-field-service-note">Supplied by the host at runtime (e.g. a record fetched
      from a system of record) — no expression to author here.</p>`;
  }

  private _renderBody(
    name: string,
    field: CalcFieldDefinition,
    completions: ReturnType<typeof completionsFor>,
    result: FieldPreview,
    edit: (change: (fields: CalcFields) => CalcFields) => void
  ) {
    return html`
      <div class="calc-field-row-body">
        <div class="field-block calc-expression-block">
          <span class="field-label" id="${name}-expr-label">Expression</span>
          <wayfinder-calculation-expression-editor
            .value=${field.expr ?? ''}
            .completions=${completions}
            label-text="${name} expression"
            @expression-input=${(event: CustomEvent<{ value: string }>) => edit((fields) => fieldEdits.setExpr(fields, name, event.detail.value))}
          ></wayfinder-calculation-expression-editor>
          ${
            result?.status === 'ok'
              ? html`<span class="calc-preview calc-preview-ok" data-wayfinder-calc-field-preview>= ${result.display}</span>`
              : result?.status === 'error'
                ? html`<span class="calc-preview calc-preview-error" data-wayfinder-calc-field-preview>${result.message}</span>`
                : nothing
          }
        </div>

        <label class="field-block">
          <span class="field-label">Format</span>
          <select
            class="field-control"
            .value=${field.format ?? ''}
            @change=${(event: Event) => edit((fields) => fieldEdits.setFormat(fields, name, (event.currentTarget as HTMLSelectElement).value))}
          >
            <option value="">-- Not set --</option>
            <option value="gbp">Currency (£)</option>
          </select>
        </label>
      </div>
    `;
  }
}
