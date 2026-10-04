import { html } from 'lit';
import { repeat } from 'lit/directives/repeat.js';
import { type CalcTableDefinition, type CalcTables, changedName, omitKey, renameKey, tableEdits } from './calculation-edits.js';
import type { CalculationsContext } from './calculations-context.js';

/** The "Tables" section: lookup tables of key → number, interpolated linearly or as steps. */
export class TablesSection {
  constructor(private readonly _context: CalculationsContext) {}

  private get _tables(): CalcTables {
    return this._context.calculations.tables ?? {};
  }

  private _update(tables: CalcTables) {
    this._context.updateCalculations({ ...this._context.calculations, tables });
  }

  private _add() {
    const { tables, name } = tableEdits.add(this._tables);
    this._update(tables);
    this._context.announce(`${name} added.`);
  }

  private _delete(name: string) {
    this._update(omitKey(this._tables, name));
    this._context.announce(`${name} deleted.`);
  }

  private _rename(oldName: string, typed: string) {
    const newName = changedName(oldName, typed);
    if (newName) this._update(renameKey(this._tables, oldName, newName));
  }

  private _renameRow(table: string, key: string, typed: string) {
    const newKey = changedName(key, typed);
    if (newKey) this._update(tableEdits.renameRow(this._tables, table, key, newKey));
  }

  render() {
    const tables = this._tables;
    const names = Object.keys(tables);

    return html`
      <details class="calc-section">
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Tables</h3>
          <span class="calc-section-meta">${names.length}</span>
        </summary>

        <ul class="calc-field-list">
          ${repeat(
            names,
            (name) => name,
            (name) => this._renderTable(name, tables[name])
          )}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._add()}>+ Add table</button>
      </details>
    `;
  }

  private _renderTable(name: string, table: CalcTableDefinition) {
    return html`
      <li class="calc-field-row" data-wayfinder-calc-table=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input class="field-control" .value=${name} @change=${(event: Event) => this._rename(name, (event.currentTarget as HTMLInputElement).value)} />
          </label>

          <label class="field-block">
            <span class="field-label">Interpolate</span>
            <select
              class="field-control"
              .value=${table.interpolate ?? 'linear'}
              @change=${(event: Event) => this._update(tableEdits.setInterpolate(this._tables, name, (event.currentTarget as HTMLSelectElement).value))}
            >
              <option value="linear">Linear</option>
              <option value="step">Step</option>
            </select>
          </label>

          <button type="button" class="icon-button danger-button" aria-label="Delete table ${name}" @click=${() => this._delete(name)}>Delete</button>
        </div>

        <table class="calc-table-values">
          <thead><tr><th scope="col">Key</th><th scope="col">Value</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            ${repeat(
              Object.entries(table.values),
              ([key]) => key,
              ([key, value]) => this._renderRow(name, key, value)
            )}
          </tbody>
        </table>
        <button type="button" class="secondary-button" @click=${() => this._update(tableEdits.addRow(this._tables, name))}>+ Add row</button>
      </li>
    `;
  }

  private _renderRow(table: string, key: string, value: number) {
    return html`
      <tr>
        <td>
          <input
            class="field-control"
            .value=${key}
            aria-label="Key for row currently ${key} in table ${table}"
            @change=${(event: Event) => this._renameRow(table, key, (event.currentTarget as HTMLInputElement).value)}
          />
        </td>
        <td>
          <input
            type="number"
            class="field-control"
            .value=${String(value)}
            aria-label="Value for key ${key} in table ${table}"
            @change=${(event: Event) => this._update(tableEdits.setRowValue(this._tables, table, key, Number((event.currentTarget as HTMLInputElement).value)))}
          />
        </td>
        <td>
          <button
            type="button"
            class="text-button"
            aria-label="Remove row ${key} from table ${table}"
            @click=${() => this._update(tableEdits.deleteRow(this._tables, table, key))}
          >Remove</button>
        </td>
      </tr>
    `;
  }
}
