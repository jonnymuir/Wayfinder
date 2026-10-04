import { html } from 'lit';
import { repeat } from 'lit/directives/repeat.js';
import { tryEvaluateFieldsForPreview, tryEvaluateSeriesForPreview } from '../calculation-runtime.js';
import '../wayfinder-calculation-expression-editor.js';
import { type CalcSeriesDefinition, type CalcSeriesMap, changedName, omitKey, renameKey, seriesEdits } from './calculation-edits.js';
import { type CalculationsContext, completionsFor } from './calculations-context.js';

type ExpressionEvent = CustomEvent<{ value: string }>;

/** The "Series" section: a loop variable over a range, with a column of expressions evaluated at each step. */
export class SeriesSection {
  constructor(private readonly _context: CalculationsContext) {}

  private get _series(): CalcSeriesMap {
    return this._context.calculations.series ?? {};
  }

  private _update(series: CalcSeriesMap) {
    this._context.updateCalculations({ ...this._context.calculations, series });
  }

  private _add() {
    const { series, name } = seriesEdits.add(this._series);
    this._update(series);
    this._context.announce(`${name} added.`);
  }

  private _delete(name: string) {
    this._update(omitKey(this._series, name));
    this._context.announce(`${name} deleted.`);
  }

  private _rename(oldName: string, typed: string) {
    const newName = changedName(oldName, typed);
    if (newName) this._update(renameKey(this._series, oldName, newName));
  }

  private _renameColumn(series: string, column: string, typed: string) {
    const newColumn = changedName(column, typed);
    if (newColumn) this._update(seriesEdits.renameColumn(this._series, series, column, newColumn));
  }

  render() {
    const series = this._series;
    const names = Object.keys(series);
    const { calculations, sampleInputs } = this._context;
    const fieldScope = tryEvaluateFieldsForPreview(calculations, sampleInputs).scope;

    return html`
      <details class="calc-section">
        <summary class="calc-section-summary">
          <h3 class="calc-section-title">Series</h3>
          <span class="calc-section-meta">${names.length}</span>
        </summary>

        <ul class="calc-field-list">
          ${repeat(
            names,
            (name) => name,
            (name) => this._renderSeries(name, series[name], fieldScope)
          )}
        </ul>

        <button type="button" class="secondary-button" @click=${() => this._add()}>+ Add series</button>
      </details>
    `;
  }

  private _expression(
    label: string,
    name: string,
    field: 'from' | 'to',
    definition: CalcSeriesDefinition,
    completions: ReturnType<typeof completionsFor>
  ) {
    return html`
      <label class="field-block">
        <span class="field-label">${label}</span>
        <wayfinder-calculation-expression-editor
          .value=${definition[field]}
          .completions=${completions}
          label-text="${name} ${field}"
          @expression-input=${(event: ExpressionEvent) => this._update(seriesEdits.setField(this._series, name, field, event.detail.value))}
        ></wayfinder-calculation-expression-editor>
      </label>
    `;
  }

  private _renderSeries(name: string, definition: CalcSeriesDefinition, fieldScope: Record<string, unknown>) {
    const { calculations } = this._context;
    const preview = tryEvaluateSeriesForPreview(definition, fieldScope, calculations);

    // Series are evaluated after every field, so (unlike a field's own expression, which can only
    // see earlier-declared fields) all of them are always in scope here.
    const completions = completionsFor(
      this._context,
      { names: Object.keys(calculations.fields), detail: 'field' },
      { names: Object.keys(calculations.tables ?? {}), detail: 'table' }
    );
    const columnCompletions = [...completions, { name: definition.over, detail: 'loop variable' }];

    return html`
      <li class="calc-field-row" data-wayfinder-calc-series=${name}>
        <div class="calc-field-row-header">
          <label class="field-block">
            <span class="field-label">Name</span>
            <input class="field-control" .value=${name} @change=${(event: Event) => this._rename(name, (event.currentTarget as HTMLInputElement).value)} />
          </label>

          <label class="field-block">
            <span class="field-label">Loop variable (over)</span>
            <input
              class="field-control"
              .value=${definition.over}
              @change=${(event: Event) => this._update(seriesEdits.setField(this._series, name, 'over', (event.currentTarget as HTMLInputElement).value))}
            />
          </label>

          <button type="button" class="icon-button danger-button" aria-label="Delete series ${name}" @click=${() => this._delete(name)}>Delete</button>
        </div>

        <div class="calc-field-row-body">
          ${this._expression('From', name, 'from', definition, completions)}
          ${this._expression('To', name, 'to', definition, completions)}
        </div>

        <ul class="calc-series-columns">
          ${repeat(
            Object.entries(definition.values),
            ([column]) => column,
            ([column, expr]) => this._renderColumn(name, column, expr, columnCompletions)
          )}
        </ul>
        <button type="button" class="secondary-button" @click=${() => this._update(seriesEdits.addColumn(this._series, name))}>+ Add column</button>

        ${
          preview.status === 'ok'
            ? html`
              <p class="calc-preview calc-preview-ok" data-wayfinder-calc-series-preview>
                ${preview.rows.length} row${preview.rows.length === 1 ? '' : 's'} computed.
              </p>
            `
            : html`<p class="calc-preview calc-preview-error" data-wayfinder-calc-series-preview>${preview.message}</p>`
        }
      </li>
    `;
  }

  private _renderColumn(series: string, column: string, expr: string, completions: ReturnType<typeof completionsFor>) {
    return html`
      <li class="calc-series-column-row">
        <label class="field-block">
          <span class="field-label">Column</span>
          <input class="field-control" .value=${column} @change=${(event: Event) => this._renameColumn(series, column, (event.currentTarget as HTMLInputElement).value)} />
        </label>
        <label class="field-block calc-expression-block">
          <span class="field-label">Expression</span>
          <wayfinder-calculation-expression-editor
            .value=${expr}
            .completions=${completions}
            label-text="${series} ${column} expression"
            @expression-input=${(event: ExpressionEvent) => this._update(seriesEdits.setColumnExpr(this._series, series, column, event.detail.value))}
          ></wayfinder-calculation-expression-editor>
        </label>
        <button
          type="button"
          class="text-button"
          aria-label="Remove column ${column} from series ${series}"
          @click=${() => this._update(seriesEdits.deleteColumn(this._series, series, column))}
        >Remove</button>
      </li>
    `;
  }
}
