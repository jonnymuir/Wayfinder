import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { ref, createRef, type Ref } from 'lit/directives/ref.js';
import { repeat } from 'lit/directives/repeat.js';
import type { AuthoredServiceBlueprint, ComponentDescriptor, ServiceBlueprintCalculationsBlock } from './types.js';
import { collectStageInputFields, type FieldReference } from './component-property-references.js';
import { computeStableFieldOrder, type FieldInput } from './calculation-ordering.js';
import { tryEvaluateFieldsForPreview, tryEvaluateSeriesForPreview } from './calculation-runtime.js';
import './wayfinder-calculation-expression-editor.js';
import type { WayfinderCalculationExpressionEditorElement } from './wayfinder-calculation-expression-editor.js';

type CalcFields = ServiceBlueprintCalculationsBlock['fields'];
type CalcFieldDefinition = CalcFields[string];
type CalcTables = NonNullable<ServiceBlueprintCalculationsBlock['tables']>;
type CalcTableDefinition = CalcTables[string];
type CalcSeriesMap = NonNullable<ServiceBlueprintCalculationsBlock['series']>;
type CalcSeriesDefinition = CalcSeriesMap[string];

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
export class WayfinderCalculationsEditorElement extends LitElement {
  @property({ attribute: false })
  serviceBlueprint: AuthoredServiceBlueprint | null = null;

  @property({ attribute: false })
  componentCatalog: ComponentDescriptor[] = [];

  @state() private _statusMessage: string | null = null;
  @state() private _cycleError: string[] | null = null;

  private get _calculations(): ServiceBlueprintCalculationsBlock {
    return this.serviceBlueprint?.calculations ?? { fields: {} };
  }

  private get _allInputFields(): FieldReference[] {
    const allComponents = (this.serviceBlueprint?.stages ?? []).flatMap(stage => stage.components ?? []);
    return collectStageInputFields(allComponents, this.componentCatalog);
  }

  /** Every input's own declared default, coerced to the type the calculation scope expects —
   * mirrors CalculationScopeBuilder.cs's own coercion (numeric field types vs everything else)
   * exactly, so the live preview here matches what validate_service_blueprint's own static
   * check would see. */
  private get _sampleInputs(): Record<string, unknown> {
    const inputs: Record<string, unknown> = {};
    for (const field of this._allInputFields) {
      if (!field.default) {
        continue;
      }
      if (NUMERIC_INPUT_TYPES.has(field.type)) {
        const numeric = Number(field.default);
        if (!Number.isNaN(numeric)) {
          inputs[field.fieldKey] = numeric;
        }
      } else if (field.default === 'true' || field.default === 'false') {
        inputs[field.fieldKey] = field.default === 'true';
      } else {
        inputs[field.fieldKey] = field.default;
      }
    }
    return inputs;
  }

  private _emitServiceBlueprintUpdated(next: AuthoredServiceBlueprint) {
    this.dispatchEvent(
      new CustomEvent('service-blueprint-updated', { detail: { serviceBlueprint: next }, bubbles: true, composed: true })
    );
  }

  private _updateCalculations(next: ServiceBlueprintCalculationsBlock) {
    if (!this.serviceBlueprint) {
      return;
    }
    this._emitServiceBlueprintUpdated({ ...this.serviceBlueprint, calculations: next });
  }

  private _announce(message: string) {
    this._statusMessage = message;
  }

  // ── Fields ──────────────────────────────────────────────────────────────────

  /** The one place a `fields` edit becomes a real update — always recomputes the stable
   * topological order (calculation-ordering.ts) and rewrites the record's own key order to
   * match, so the persisted JSON's declaration order is always correct without the designer
   * ever having to think about it. A genuine cycle blocks reordering (there's no valid order)
   * but still accepts the edit itself — Save stays enabled/disabled by the existing Validation
   * tab's own check, which would flag the same cycle as an unresolvable-name error either way. */
  private _updateFields(nextFields: CalcFields, currentOrder: string[]) {
    const fieldInputs: FieldInput[] = Object.entries(nextFields).map(([name, field]) => ({
      name,
      expr: field.expr ?? '',
    }));
    const orderResult = computeStableFieldOrder(fieldInputs, currentOrder);

    if (!orderResult.ok) {
      this._cycleError = orderResult.cycle;
      this._updateCalculations({ ...this._calculations, fields: nextFields });
      return;
    }

    this._cycleError = null;
    if (orderResult.moved.length > 0) {
      this._announce(
        orderResult.moved.map(move => `Moved "${move.name}" after "${move.movedAfter}" because it now depends on it.`).join(' ')
      );
    }

    const reordered: CalcFields = {};
    for (const name of orderResult.order) {
      reordered[name] = nextFields[name];
    }
    this._updateCalculations({ ...this._calculations, fields: reordered });
  }

  private _addField(order: string[]) {
    const fields = { ...this._calculations.fields };
    let suffix = 1;
    while (fields[`field${suffix}`]) {
      suffix += 1;
    }
    const name = `field${suffix}`;
    fields[name] = { expr: '' };
    this._updateFields(fields, order);
    this._announce(`${name} added.`);
  }

  private _deleteField(name: string, order: string[]) {
    const fields = { ...this._calculations.fields };
    delete fields[name];
    this._updateFields(
      fields,
      order.filter(existing => existing !== name)
    );
    this._announce(`${name} deleted.`);
  }

  private _renameField(oldName: string, newName: string, order: string[]) {
    const trimmed = newName.trim();
    if (!trimmed || trimmed === oldName) {
      return;
    }
    const fields = this._calculations.fields;
    const next: CalcFields = {};
    for (const [key, value] of Object.entries(fields)) {
      next[key === oldName ? trimmed : key] = value;
    }
    this._updateFields(
      next,
      order.map(existing => (existing === oldName ? trimmed : existing))
    );
  }

  private _setFieldExpr(name: string, expr: string, order: string[]) {
    const fields = { ...this._calculations.fields };
    fields[name] = { ...fields[name], expr };
    this._updateFields(fields, order);
  }

  private _setFieldSource(name: string, isService: boolean, order: string[]) {
    const fields = { ...this._calculations.fields };
    fields[name] = isService ? { source: 'service' } : { expr: '' };
    this._updateFields(fields, order);
  }

  private _setFieldFormat(name: string, format: string, order: string[]) {
    const fields = { ...this._calculations.fields };
    const current = fields[name] ?? {};
    fields[name] = format ? { ...current, format } : { ...current, format: undefined };
    this._updateFields(fields, order);
  }

  private _fieldNameError(name: string, order: string[], inputFieldKeys: Set<string>): string | null {
    if (!name.trim()) {
      return 'Name is required.';
    }
    if (inputFieldKeys.has(name)) {
      return `Collides with an input field's own fieldKey ("${name}").`;
    }
    if (order.filter(existing => existing === name).length > 1) {
      return 'Duplicate field name.';
    }
    return null;
  }

  private _renderFieldsSection() {
    const fields = this._calculations.fields;
    const order = Object.keys(fields);
    const preview = tryEvaluateFieldsForPreview(this._calculations, this._sampleInputs);
    // Only an input that ALSO has a declared default can be statically known to occupy the
    // calculation scope (CalculationScopeBuilder.Build: an input with no submission and no
    // default is simply absent from scope, not an error) — matches the same limitation
    // validate_service_blueprint's own static check has, documented in
    // docs/guides/calculation-language.md. Without this, a summary-list child that legitimately
    // reuses a calc field's own name to display its value (the standard check-your-answers
    // pattern — see e.g. juggling-insurance-modeller.json's "totalPremium" row) would be
    // wrongly flagged as a collision it can never actually cause at runtime.
    const inputFieldKeys = new Set(this._allInputFields.filter(field => field.default).map(field => field.fieldKey));
    const tableNames = Object.keys(this._calculations.tables ?? {});

    return html`
      <details class="calc-section" open>
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Fields</h3>
          <span class="calc-section-meta">${order.length}</span>
        </summary>

        ${this._cycleError
          ? html`
              <div class="calc-cycle-banner" role="alert">
                Circular dependency: ${this._cycleError.join(' → ')} → ${this._cycleError[0]}.
                These fields reference each other in a loop and can never be ordered — fix one of
                these expressions before saving.
              </div>
            `
          : nothing}

        <ul class="calc-field-list">
          ${repeat(
            order,
            name => name,
            (name, index) =>
              this._renderFieldRow(name, fields[name], order, index, preview.results[name], inputFieldKeys, tableNames)
          )}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._addField(order)}>+ Add field</button>
      </details>
    `;
  }

  private _renderFieldRow(
    name: string,
    field: CalcFieldDefinition,
    order: string[],
    index: number,
    result: ReturnType<typeof tryEvaluateFieldsForPreview>['results'][string] | undefined,
    inputFieldKeys: Set<string>,
    tableNames: string[]
  ) {
    const isService = (field.source ?? '').toLowerCase() === 'service';
    const nameError = this._fieldNameError(name, order, inputFieldKeys);
    const exprRef: Ref<WayfinderCalculationExpressionEditorElement> = createRef();

    const insertOptions = [
      ...this._allInputFields.map(input => ({ value: input.fieldKey, label: `${input.label} (${input.fieldKey})` })),
      ...order.slice(0, index).map(earlier => ({ value: earlier, label: `${earlier} (field)` })),
      ...tableNames.map(table => ({ value: table, label: `${table} (table)` })),
    ];

    return html`
      <li class="calc-field-row" data-wayfinder-calc-field=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input
              class="field-control ${nameError ? 'field-control-error' : ''}"
              .value=${name}
              @change=${(event: Event) => this._renameField(name, (event.currentTarget as HTMLInputElement).value, order)}
            />
            ${nameError ? html`<span class="field-error">${nameError}</span>` : nothing}
          </label>

          <label class="field-toggle">
            <input
              type="checkbox"
              .checked=${isService}
              @change=${(event: Event) => this._setFieldSource(name, (event.currentTarget as HTMLInputElement).checked, order)}
            />
            <span>Supplied by the host (source: service)</span>
          </label>

          <button
            type="button"
            class="icon-button danger-button"
            aria-label="Delete field ${name}"
            @click=${() => this._deleteField(name, order)}
          >Delete</button>
        </div>

        ${isService
          ? html`<p class="calc-field-service-note">Supplied by the host at runtime (e.g. a record fetched
              from a system of record) — no expression to author here.</p>`
          : html`
              <div class="calc-field-row-body">
                <div class="field-block calc-expression-block">
                  <span class="field-label" id="${name}-expr-label">Expression</span>
                  <wayfinder-calculation-expression-editor
                    ${ref(exprRef)}
                    .value=${field.expr ?? ''}
                    label-text="${name} expression"
                    @expression-input=${(event: CustomEvent<{ value: string }>) =>
                      this._setFieldExpr(name, event.detail.value, order)}
                  ></wayfinder-calculation-expression-editor>
                  ${result?.status === 'ok'
                    ? html`<span class="calc-preview calc-preview-ok" data-wayfinder-calc-field-preview>= ${result.display}</span>`
                    : result?.status === 'error'
                      ? html`<span class="calc-preview calc-preview-error" data-wayfinder-calc-field-preview>${result.message}</span>`
                      : nothing}
                </div>

                <label class="field-block">
                  <span class="field-label">Insert a reference</span>
                  <select
                    class="field-control"
                    @change=${(event: Event) => {
                      const select = event.currentTarget as HTMLSelectElement;
                      if (select.value) {
                        exprRef.value?.insertAtCursor(select.value);
                      }
                      select.value = '';
                    }}
                  >
                    <option value="">-- Insert --</option>
                    ${insertOptions.map(option => html`<option value=${option.value}>${option.label}</option>`)}
                  </select>
                </label>

                <label class="field-block">
                  <span class="field-label">Format</span>
                  <select
                    class="field-control"
                    .value=${field.format ?? ''}
                    @change=${(event: Event) => this._setFieldFormat(name, (event.currentTarget as HTMLSelectElement).value, order)}
                  >
                    <option value="">-- Not set --</option>
                    <option value="gbp">Currency (£)</option>
                  </select>
                </label>
              </div>
            `}
      </li>
    `;
  }

  // ── Tables ──────────────────────────────────────────────────────────────────

  private _updateTables(nextTables: CalcTables) {
    this._updateCalculations({ ...this._calculations, tables: nextTables });
  }

  private _addTable() {
    const tables = { ...(this._calculations.tables ?? {}) };
    let suffix = 1;
    while (tables[`table${suffix}`]) {
      suffix += 1;
    }
    const name = `table${suffix}`;
    tables[name] = { interpolate: 'linear', values: {} };
    this._updateTables(tables);
    this._announce(`${name} added.`);
  }

  private _deleteTable(name: string) {
    const tables = { ...(this._calculations.tables ?? {}) };
    delete tables[name];
    this._updateTables(tables);
    this._announce(`${name} deleted.`);
  }

  private _renameTable(oldName: string, newName: string) {
    const trimmed = newName.trim();
    if (!trimmed || trimmed === oldName) {
      return;
    }
    const tables = this._calculations.tables ?? {};
    const next: CalcTables = {};
    for (const [key, value] of Object.entries(tables)) {
      next[key === oldName ? trimmed : key] = value;
    }
    this._updateTables(next);
  }

  private _setTableInterpolate(name: string, interpolate: string) {
    const tables = { ...(this._calculations.tables ?? {}) };
    tables[name] = { ...tables[name], interpolate };
    this._updateTables(tables);
  }

  private _addTableRow(name: string) {
    const tables = { ...(this._calculations.tables ?? {}) };
    const table = tables[name] ?? { values: {} };
    let key = '0';
    let suffix = 0;
    while (key in table.values) {
      suffix += 1;
      key = String(suffix);
    }
    tables[name] = { ...table, values: { ...table.values, [key]: 0 } };
    this._updateTables(tables);
  }

  private _deleteTableRow(name: string, key: string) {
    const tables = { ...(this._calculations.tables ?? {}) };
    const table = tables[name];
    if (!table) {
      return;
    }
    const values = { ...table.values };
    delete values[key];
    tables[name] = { ...table, values };
    this._updateTables(tables);
  }

  private _setTableRowKey(name: string, oldKey: string, newKey: string) {
    const trimmed = newKey.trim();
    if (!trimmed || trimmed === oldKey) {
      return;
    }
    const tables = { ...(this._calculations.tables ?? {}) };
    const table = tables[name];
    if (!table) {
      return;
    }
    const values: Record<string, number> = {};
    for (const [key, value] of Object.entries(table.values)) {
      values[key === oldKey ? trimmed : key] = value;
    }
    tables[name] = { ...table, values };
    this._updateTables(tables);
  }

  private _setTableRowValue(name: string, key: string, value: number) {
    const tables = { ...(this._calculations.tables ?? {}) };
    const table = tables[name];
    if (!table) {
      return;
    }
    tables[name] = { ...table, values: { ...table.values, [key]: value } };
    this._updateTables(tables);
  }

  private _renderTablesSection() {
    const tables = this._calculations.tables ?? {};
    const names = Object.keys(tables);

    return html`
      <details class="calc-section">
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Tables</h3>
          <span class="calc-section-meta">${names.length}</span>
        </summary>

        <ul class="calc-field-list">
          ${repeat(names, name => name, name => this._renderTableRow(name, tables[name]))}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._addTable()}>+ Add table</button>
      </details>
    `;
  }

  private _renderTableRow(name: string, table: CalcTableDefinition) {
    const rows = Object.entries(table.values);

    return html`
      <li class="calc-field-row" data-wayfinder-calc-table=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input
              class="field-control"
              .value=${name}
              @change=${(event: Event) => this._renameTable(name, (event.currentTarget as HTMLInputElement).value)}
            />
          </label>

          <label class="field-block">
            <span class="field-label">Interpolate</span>
            <select
              class="field-control"
              .value=${table.interpolate ?? 'linear'}
              @change=${(event: Event) => this._setTableInterpolate(name, (event.currentTarget as HTMLSelectElement).value)}
            >
              <option value="linear">Linear</option>
              <option value="step">Step</option>
            </select>
          </label>

          <button type="button" class="icon-button danger-button" aria-label="Delete table ${name}" @click=${() => this._deleteTable(name)}>Delete</button>
        </div>

        <table class="calc-table-values">
          <thead><tr><th scope="col">Key</th><th scope="col">Value</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            ${repeat(
              rows,
              ([key]) => key,
              ([key, value]) => html`
                <tr>
                  <td>
                    <input
                      class="field-control"
                      .value=${key}
                      aria-label="Key for row currently ${key} in table ${name}"
                      @change=${(event: Event) => this._setTableRowKey(name, key, (event.currentTarget as HTMLInputElement).value)}
                    />
                  </td>
                  <td>
                    <input
                      type="number"
                      class="field-control"
                      .value=${String(value)}
                      aria-label="Value for key ${key} in table ${name}"
                      @change=${(event: Event) => this._setTableRowValue(name, key, Number((event.currentTarget as HTMLInputElement).value))}
                    />
                  </td>
                  <td>
                    <button type="button" class="text-button" aria-label="Remove row ${key} from table ${name}" @click=${() => this._deleteTableRow(name, key)}>Remove</button>
                  </td>
                </tr>
              `
            )}
          </tbody>
        </table>
        <button type="button" class="secondary-button" @click=${() => this._addTableRow(name)}>+ Add row</button>
      </li>
    `;
  }

  // ── Series ──────────────────────────────────────────────────────────────────

  private _updateSeries(nextSeries: CalcSeriesMap) {
    this._updateCalculations({ ...this._calculations, series: nextSeries });
  }

  private _addSeries() {
    const series = { ...(this._calculations.series ?? {}) };
    let suffix = 1;
    while (series[`series${suffix}`]) {
      suffix += 1;
    }
    const name = `series${suffix}`;
    series[name] = { over: 'i', from: '1', to: '1', values: {} };
    this._updateSeries(series);
    this._announce(`${name} added.`);
  }

  private _deleteSeries(name: string) {
    const series = { ...(this._calculations.series ?? {}) };
    delete series[name];
    this._updateSeries(series);
    this._announce(`${name} deleted.`);
  }

  private _renameSeries(oldName: string, newName: string) {
    const trimmed = newName.trim();
    if (!trimmed || trimmed === oldName) {
      return;
    }
    const series = this._calculations.series ?? {};
    const next: CalcSeriesMap = {};
    for (const [key, value] of Object.entries(series)) {
      next[key === oldName ? trimmed : key] = value;
    }
    this._updateSeries(next);
  }

  private _setSeriesField<K extends keyof CalcSeriesDefinition>(name: string, key: K, value: CalcSeriesDefinition[K]) {
    const series = { ...(this._calculations.series ?? {}) };
    const current = series[name] ?? { over: '', from: '', to: '', values: {} };
    series[name] = { ...current, [key]: value };
    this._updateSeries(series);
  }

  private _addSeriesColumn(name: string) {
    const series = { ...(this._calculations.series ?? {}) };
    const current = series[name] ?? { over: '', from: '', to: '', values: {} };
    let suffix = 1;
    while (current.values[`column${suffix}`]) {
      suffix += 1;
    }
    const column = `column${suffix}`;
    series[name] = { ...current, values: { ...current.values, [column]: '' } };
    this._updateSeries(series);
  }

  private _deleteSeriesColumn(name: string, column: string) {
    const series = { ...(this._calculations.series ?? {}) };
    const current = series[name];
    if (!current) {
      return;
    }
    const values = { ...current.values };
    delete values[column];
    series[name] = { ...current, values };
    this._updateSeries(series);
  }

  private _renameSeriesColumn(name: string, oldColumn: string, newColumn: string) {
    const trimmed = newColumn.trim();
    if (!trimmed || trimmed === oldColumn) {
      return;
    }
    const series = { ...(this._calculations.series ?? {}) };
    const current = series[name];
    if (!current) {
      return;
    }
    const values: Record<string, string> = {};
    for (const [key, value] of Object.entries(current.values)) {
      values[key === oldColumn ? trimmed : key] = value;
    }
    series[name] = { ...current, values };
    this._updateSeries(series);
  }

  private _setSeriesColumnExpr(name: string, column: string, expr: string) {
    const series = { ...(this._calculations.series ?? {}) };
    const current = series[name];
    if (!current) {
      return;
    }
    series[name] = { ...current, values: { ...current.values, [column]: expr } };
    this._updateSeries(series);
  }

  private _renderSeriesSection(fieldScope: Record<string, unknown>) {
    const series = this._calculations.series ?? {};
    const names = Object.keys(series);

    return html`
      <details class="calc-section">
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Series</h3>
          <span class="calc-section-meta">${names.length}</span>
        </summary>

        <ul class="calc-field-list">
          ${repeat(names, name => name, name => this._renderSeriesRow(name, series[name], fieldScope))}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._addSeries()}>+ Add series</button>
      </details>
    `;
  }

  private _renderSeriesRow(name: string, definition: CalcSeriesDefinition, fieldScope: Record<string, unknown>) {
    const preview = tryEvaluateSeriesForPreview(definition, fieldScope, this._calculations);
    const fromRef: Ref<WayfinderCalculationExpressionEditorElement> = createRef();
    const toRef: Ref<WayfinderCalculationExpressionEditorElement> = createRef();

    return html`
      <li class="calc-field-row" data-wayfinder-calc-series=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input class="field-control" .value=${name} @change=${(event: Event) => this._renameSeries(name, (event.currentTarget as HTMLInputElement).value)} />
          </label>

          <label class="field-block">
            <span class="field-label">Loop variable (over)</span>
            <input
              class="field-control"
              .value=${definition.over}
              @change=${(event: Event) => this._setSeriesField(name, 'over', (event.currentTarget as HTMLInputElement).value)}
            />
          </label>

          <button type="button" class="icon-button danger-button" aria-label="Delete series ${name}" @click=${() => this._deleteSeries(name)}>Delete</button>
        </div>

        <div class="calc-field-row-body">
          <label class="field-block">
            <span class="field-label">From</span>
            <wayfinder-calculation-expression-editor
              ${ref(fromRef)}
              .value=${definition.from}
              label-text="${name} from"
              @expression-input=${(event: CustomEvent<{ value: string }>) => this._setSeriesField(name, 'from', event.detail.value)}
            ></wayfinder-calculation-expression-editor>
          </label>

          <label class="field-block">
            <span class="field-label">To</span>
            <wayfinder-calculation-expression-editor
              ${ref(toRef)}
              .value=${definition.to}
              label-text="${name} to"
              @expression-input=${(event: CustomEvent<{ value: string }>) => this._setSeriesField(name, 'to', event.detail.value)}
            ></wayfinder-calculation-expression-editor>
          </label>
        </div>

        <ul class="calc-series-columns">
          ${repeat(
            Object.entries(definition.values),
            ([column]) => column,
            ([column, expr]) => {
              const columnRef: Ref<WayfinderCalculationExpressionEditorElement> = createRef();
              return html`
                <li class="calc-series-column-row">
                  <label class="field-block">
                    <span class="field-label">Column</span>
                    <input
                      class="field-control"
                      .value=${column}
                      @change=${(event: Event) => this._renameSeriesColumn(name, column, (event.currentTarget as HTMLInputElement).value)}
                    />
                  </label>
                  <label class="field-block calc-expression-block">
                    <span class="field-label">Expression</span>
                    <wayfinder-calculation-expression-editor
                      ${ref(columnRef)}
                      .value=${expr}
                      label-text="${name} ${column} expression"
                      @expression-input=${(event: CustomEvent<{ value: string }>) => this._setSeriesColumnExpr(name, column, event.detail.value)}
                    ></wayfinder-calculation-expression-editor>
                  </label>
                  <button type="button" class="text-button" aria-label="Remove column ${column} from series ${name}" @click=${() => this._deleteSeriesColumn(name, column)}>Remove</button>
                </li>
              `;
            }
          )}
        </ul>
        <button type="button" class="secondary-button" @click=${() => this._addSeriesColumn(name)}>+ Add column</button>

        ${preview.status === 'ok'
          ? html`
              <p class="calc-preview calc-preview-ok" data-wayfinder-calc-series-preview>
                ${preview.rows.length} row${preview.rows.length === 1 ? '' : 's'} computed.
              </p>
            `
          : html`<p class="calc-preview calc-preview-error" data-wayfinder-calc-series-preview>${preview.message}</p>`}
      </li>
    `;
  }

  render() {
    if (!this.serviceBlueprint) {
      return html`<div class="calc-empty">No service blueprint loaded.</div>`;
    }

    const fieldScope = tryEvaluateFieldsForPreview(this._calculations, this._sampleInputs).scope;

    return html`
      <div class="calc-root" data-wayfinder-component="calculations-editor">
        <div id="calc-announcer" class="sr-only" role="status" aria-live="polite" aria-atomic="true">${this._statusMessage ?? ''}</div>
        ${this._renderFieldsSection()}
        ${this._renderTablesSection()}
        ${this._renderSeriesSection(fieldScope)}
      </div>
    `;
  }

  static styles = css`
    :host {
      display: block;
      height: 100%;
      overflow-y: auto;
      background: #f8fafc;
      font-family: "GDS Transport", arial, sans-serif;
      padding: 1rem;
      box-sizing: border-box;
    }

    .calc-empty {
      padding: 1rem;
      color: #475569;
    }

    .calc-root {
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }

    .calc-section {
      background: #ffffff;
      border: 1px solid #e2e8f0;
      border-radius: 12px;
      padding: 0.75rem 1rem 1rem;
    }

    .calc-section-summary {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      cursor: pointer;
      list-style: none;
    }

    .calc-section-summary::-webkit-details-marker {
      display: none;
    }

    .calc-section-title {
      margin: 0;
      font-size: 1rem;
    }

    .calc-section-meta {
      color: #475569;
      font-size: 0.8125rem;
    }

    .calc-cycle-banner {
      margin: 0.75rem 0;
      padding: 0.75rem 1rem;
      border-radius: 10px;
      background: #fbeaec;
      color: #b91c1c;
      font-size: 0.875rem;
    }

    .calc-field-list,
    .calc-series-columns {
      list-style: none;
      margin: 0.75rem 0;
      padding: 0;
      display: grid;
      gap: 0.75rem;
    }

    .calc-field-row {
      border: 1px solid #e2e8f0;
      border-radius: 10px;
      padding: 0.75rem;
      background: #f8fafc;
    }

    .calc-field-row-header {
      display: grid;
      grid-template-columns: 1fr auto auto;
      align-items: end;
      gap: 0.75rem;
      margin-bottom: 0.625rem;
    }

    .calc-field-row-body {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      align-items: start;
      gap: 0.75rem;
    }

    .calc-expression-block {
      grid-column: 1 / -1;
    }

    .calc-field-service-note {
      margin: 0;
      color: #475569;
      font-size: 0.8125rem;
    }

    .calc-series-column-row {
      display: grid;
      grid-template-columns: minmax(0, 0.6fr) minmax(0, 1.4fr) auto;
      align-items: end;
      gap: 0.625rem;
    }

    .field-block {
      display: grid;
      gap: 0.375rem;
      min-width: 0;
    }

    .field-label {
      font-size: 0.8125rem;
      font-weight: 700;
      color: #334155;
    }

    .field-control {
      width: 100%;
      min-height: 2.5rem;
      padding: 0.625rem 0.75rem;
      border: 1px solid #cbd5e1;
      border-radius: 10px;
      background: #ffffff;
      color: #111827;
      font: inherit;
      box-sizing: border-box;
    }

    .field-control-error {
      border-color: #dc2626;
    }

    .field-error {
      color: #b91c1c;
      font-size: 0.8125rem;
    }

    .field-toggle {
      display: flex;
      align-items: center;
      gap: 0.625rem;
      min-height: 2.5rem;
      color: #111827;
      font-size: 0.875rem;
      font-weight: 600;
    }

    .calc-preview {
      font-size: 0.8125rem;
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
    }

    .calc-preview-ok {
      color: #166534;
    }

    .calc-preview-error {
      color: #b91c1c;
    }

    .calc-table-values {
      width: 100%;
      border-collapse: collapse;
      margin-top: 0.5rem;
    }

    .calc-table-values th {
      text-align: left;
      font-size: 0.75rem;
      color: #475569;
      padding: 0.25rem 0.5rem;
    }

    .calc-table-values td {
      padding: 0.25rem 0.5rem;
    }

    .secondary-button {
      margin-top: 0.5rem;
      padding: 0.5rem 0.875rem;
      border: 1px solid #1d70b8;
      border-radius: 10px;
      background: #ffffff;
      color: #1d70b8;
      font: inherit;
      font-weight: 600;
      cursor: pointer;
    }

    .icon-button {
      padding: 0.5rem 0.75rem;
      border: 1px solid #cbd5e1;
      border-radius: 10px;
      background: #ffffff;
      font: inherit;
      cursor: pointer;
    }

    .danger-button {
      border-color: #fecaca;
      color: #b91c1c;
      background: #fff5f5;
    }

    .text-button {
      border: none;
      background: none;
      color: #b91c1c;
      font: inherit;
      font-size: 0.8125rem;
      cursor: pointer;
      padding: 0;
    }

    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-calculations-editor': WayfinderCalculationsEditorElement;
  }
}
