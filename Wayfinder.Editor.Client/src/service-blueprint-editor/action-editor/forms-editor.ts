import { type TemplateResult, nothing } from 'lit';
import { html, unsafeStatic } from 'lit/static-html.js';
import { ACTION_FORM_FIELD_TYPES, blankActionFormField, type ActionValidationResult } from '../action-editing.js';
import type { ActionFormFieldConfig } from '../types.js';

type FieldErrors = ActionValidationResult['formFieldErrors'][number];
type TextField = 'fieldKey' | 'label' | 'defaultValue';

/** The edits to a form-backed action's field list, as pure functions over the list. */
export const formFields = {
  append: (fields: ActionFormFieldConfig[]) => [...fields, blankActionFormField(fields.length)],
  patch: (fields: ActionFormFieldConfig[], at: number, change: Partial<ActionFormFieldConfig>) =>
    fields.map((field, index) => (index === at ? { ...field, ...change } : field)),
  move: (fields: ActionFormFieldConfig[], at: number, delta: -1 | 1) => {
    const to = Math.min(fields.length - 1, Math.max(0, at + delta));
    const next = [...fields];
    const [moved] = next.splice(at, 1);
    next.splice(to, 0, moved);
    return { fields: next, to };
  },
  remove: (fields: ActionFormFieldConfig[], at: number) => fields.filter((_, index) => index !== at),
};

export interface FormsEditorModel {
  index: number;
  /** Read fresh on every access, so a quick second edit never works from a stale list. */
  readonly fields: ActionFormFieldConfig[];
  errors: ActionValidationResult['formFieldErrors'];
  setFields(fields: ActionFormFieldConfig[]): void;
  announce(message: string): void;
}

const nameOf = (field: ActionFormFieldConfig | undefined) => field?.label || field?.fieldKey || 'Field';

/** The edit surface for one field row. Keeps the model's index so every control gets the same test hooks. */
class FieldRow {
  constructor(
    private readonly _model: FormsEditorModel,
    private readonly _at: number
  ) {}

  private get _field() {
    return this._model.fields[this._at];
  }

  private get _errors(): FieldErrors {
    return this._model.errors[this._at] ?? {};
  }

  private _patch = (change: Partial<ActionFormFieldConfig>) =>
    this._model.setFields(formFields.patch(this._model.fields, this._at, change));
  private _hook = () => `${this._model.index}-${this._at}`;

  move(delta: -1 | 1) {
    const { fields, to } = formFields.move(this._model.fields, this._at, delta);
    if (to === this._at) {
      return;
    }
    this._model.setFields(fields);
    this._model.announce(`${nameOf(this._field)} moved to position ${to + 1}.`);
  }

  remove() {
    this._model.setFields(formFields.remove(this._model.fields, this._at));
    this._model.announce(`${nameOf(this._field)} removed.`);
  }

  private _text(label: string, name: TextField, hook: string) {
    const error = this._errors[name];
    return html`
      <label class="field-block">
        <span class="field-label">${label}</span>
        <input
          class="field-control ${error ? 'field-control-error' : ''}"
          aria-invalid=${String(Boolean(error))}
          .value=${this._field[name] ?? ''}
          data-wayfinder-form-field-${unsafeStatic(hook)}="${this._hook()}"
          @input=${(event: Event) => this._patch({ [name]: (event.currentTarget as HTMLInputElement).value })}
        />
        ${error ? html`<span class="field-error">${error}</span>` : nothing}
      </label>
    `;
  }

  private _typeSelect() {
    const error = this._errors.type;
    return html`
      <label class="field-block">
        <span class="field-label">Field type</span>
        <select
          class="field-control ${error ? 'field-control-error' : ''}"
          aria-invalid=${String(Boolean(error))}
          data-wayfinder-form-field-type="${this._hook()}"
          @change=${(event: Event) => this._patch({ type: (event.currentTarget as HTMLSelectElement).value as ActionFormFieldConfig['type'] })}
        >
          ${ACTION_FORM_FIELD_TYPES.map((option) => html`<option value=${option.value} ?selected=${this._field.type === option.value}>${option.label}</option>`)}
        </select>
        ${error ? html`<span class="field-error">${error}</span>` : nothing}
      </label>
    `;
  }

  private _guidance() {
    const field = this._field;
    return html`
      <div class="field-grid">
        <label class="field-block field-block-full">
          <span class="field-label-row">
            <span class="field-label">Help text</span>
            <wayfinder-inline-help
              label="Form field help text guidance"
              message="Use this for short, task-specific guidance that appears below the field in the authored form. Keep it instructional rather than repeating the label."
            ></wayfinder-inline-help>
          </span>
          <textarea
            class="field-control field-textarea"
            .value=${field.hintText ?? ''}
            @input=${(event: Event) => this._patch({ hintText: (event.currentTarget as HTMLTextAreaElement).value })}
          ></textarea>
        </label>
        <label class="field-block">
          <span class="field-label-row">
            <span class="field-label">Validation pattern</span>
            <wayfinder-inline-help
              label="Validation pattern help"
              message="Add a regular expression only when the field needs a strict format such as a reference number or postcode. Keep patterns short and explain them in help text if they are not obvious."
            ></wayfinder-inline-help>
          </span>
          <input
            class="field-control"
            .value=${field.validationPattern ?? ''}
            @input=${(event: Event) => this._patch({ validationPattern: (event.currentTarget as HTMLInputElement).value })}
          />
        </label>
        <label class="field-toggle">
          <input
            type="checkbox"
            .checked=${field.required}
            data-wayfinder-form-field-required="${this._hook()}"
            @change=${(event: Event) => this._patch({ required: (event.currentTarget as HTMLInputElement).checked })}
          />
          <span>Required</span>
        </label>
      </div>
    `;
  }

  private _options() {
    const field = this._field;
    if (field.type !== 'select' && field.type !== 'radio') {
      return nothing;
    }

    const error = this._errors.options;
    return html`
      <label class="field-block field-block-full">
        <span class="field-label-row">
          <span class="field-label">Options</span>
          <wayfinder-inline-help
            label="Field options help"
            message="Enter one choice per line in the order authors should see them. Keep labels short and distinct so keyboard and screen-reader users can scan them quickly."
          ></wayfinder-inline-help>
        </span>
        <textarea
          class="field-control field-textarea ${error ? 'field-control-error' : ''}"
          aria-invalid=${String(Boolean(error))}
          data-wayfinder-form-field-options="${this._hook()}"
          .value=${field.options.join('\n')}
          @input=${(event: Event) =>
            this._patch({
              options: (event.currentTarget as HTMLTextAreaElement).value
                .split('\n')
                .map((option) => option.trim())
                .filter(Boolean),
            })}
        ></textarea>
        <span class="field-help">One option per line.</span>
        ${error ? html`<span class="field-error">${error}</span>` : nothing}
      </label>
    `;
  }

  render(): TemplateResult {
    const last = this._model.fields.length - 1;
    return html`
      <li
        class="form-field-item"
        data-wayfinder-form-field="${this._hook()}"
        tabindex="0"
        @keydown=${(event: KeyboardEvent) => {
          if (event.altKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
            event.preventDefault();
            this.move(event.key === 'ArrowUp' ? -1 : 1);
          }
        }}
      >
        <div class="field-grid">
          ${this._text('Field key', 'fieldKey', 'key')}
          ${this._text('Label', 'label', 'label')}
          ${this._typeSelect()}
          ${this._text('Default value', 'defaultValue', 'default')}
        </div>
        ${this._guidance()}
        ${this._options()}
        <div class="action-buttons">
          <button type="button" class="icon-button" ?disabled=${this._at === 0} @click=${() => this.move(-1)}>Move up</button>
          <button type="button" class="icon-button" ?disabled=${this._at === last} @click=${() => this.move(1)}>Move down</button>
          <button type="button" class="icon-button danger-button" @click=${() => this.remove()}>Remove field</button>
        </div>
      </li>
    `;
  }
}

/** The editor for a forms-backed action's field list: add, edit, reorder and remove. */
export function renderFormsEditor(model: FormsEditorModel) {
  const { index } = model;
  return html`
    <div class="forms-editor" data-wayfinder-action-forms-editor="${index}">
      <div class="section-header-row">
        <h4 class="subsection-heading">Form fields</h4>
        <span class="section-meta">${model.fields.length}</span>
      </div>
      <p class="section-copy">Add, remove, and reorder fields. Select and radio fields require options.</p>
      <button
        type="button"
        class="secondary-button"
        data-wayfinder-add-form-field="${index}"
        @click=${() => {
          const next = formFields.append(model.fields);
          model.setFields(next);
          model.announce(`Field ${next.length} added.`);
        }}
      >
        Add field
      </button>
      ${
        model.fields.length === 0
          ? html`<p class="section-empty">No fields configured yet.</p>`
          : html`<ol class="form-field-list">${model.fields.map((_, at) => new FieldRow(model, at).render())}</ol>`
      }
    </div>
  `;
}
