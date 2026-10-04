import { type TemplateResult, html, nothing } from 'lit';
import type { AuthoredParameterDefinition } from '../types.js';

type ScalarKind = 'toggle' | 'textarea' | 'select' | 'input';

interface ScalarField {
  index: number;
  definition: AuthoredParameterDefinition;
  value: unknown;
  /** Validation message for this parameter, if any. */
  error: string | undefined;
  onChange(value: unknown): void;
}

function scalarKind(definition: AuthoredParameterDefinition): ScalarKind {
  const editor = definition.editor ?? (definition.allowedValues?.length ? 'select' : undefined);
  if (editor === 'toggle' || definition.valueKind === 'Boolean') return 'toggle';
  if (editor === 'textarea') return 'textarea';
  return editor === 'select' ? 'select' : 'input';
}

function inputType(definition: AuthoredParameterDefinition): 'date' | 'number' | 'text' {
  if (definition.editor === 'date' || definition.format === 'date') return 'date';
  const numeric = definition.editor === 'number' || definition.valueKind === 'Integer' || definition.valueKind === 'Number';
  return numeric ? 'number' : 'text';
}

/** The label, the control, its help text and its validation message: the frame every non-toggle parameter shares. */
function labelled({ definition, error }: ScalarField, control: TemplateResult) {
  return html`
    <label class="field-block">
      <span class="field-label">${definition.title}</span>
      ${control}
      ${definition.description ? html`<span class="field-help">${definition.description}</span>` : nothing}
      ${error ? html`<span class="field-error">${error}</span>` : nothing}
    </label>
  `;
}

const controlClass = (field: ScalarField, extra = '') => `field-control ${extra} ${field.error ? 'field-control-error' : ''}`;
const hook = ({ index, definition }: ScalarField) => `${index}-${definition.key}`;

function renderToggle(field: ScalarField) {
  return html`
    <label class="field-toggle">
      <input
        type="checkbox"
        .checked=${Boolean(field.value)}
        data-wayfinder-action-param="${hook(field)}"
        @change=${(event: Event) => field.onChange((event.currentTarget as HTMLInputElement).checked)}
      />
      <span>${field.definition.title}</span>
    </label>
  `;
}

function renderTextarea(field: ScalarField) {
  const { value, error } = field;
  return labelled(
    field,
    html`<textarea
      class=${controlClass(field, 'field-textarea')}
      aria-invalid=${String(Boolean(error))}
      data-wayfinder-action-param="${hook(field)}"
      .value=${typeof value === 'string' ? value : String(value ?? '')}
      @input=${(event: Event) => field.onChange((event.currentTarget as HTMLTextAreaElement).value)}
    ></textarea>`
  );
}

function renderSelect(field: ScalarField) {
  return labelled(
    field,
    html`<select
      class=${controlClass(field)}
      aria-invalid=${String(Boolean(field.error))}
      data-wayfinder-action-param="${hook(field)}"
      @change=${(event: Event) => field.onChange((event.currentTarget as HTMLSelectElement).value)}
    >
      ${field.definition.allowedValues?.map((option) => html`<option value=${option} ?selected=${String(field.value ?? '') === option}>${option}</option>`)}
    </select>`
  );
}

function renderInput(field: ScalarField) {
  const type = inputType(field.definition);
  return labelled(
    field,
    html`<input
      class=${controlClass(field)}
      aria-invalid=${String(Boolean(field.error))}
      type=${type}
      data-wayfinder-action-param="${hook(field)}"
      .value=${field.value === undefined || field.value === null ? '' : String(field.value)}
      @input=${(event: Event) => {
        const text = (event.currentTarget as HTMLInputElement).value;
        field.onChange(type === 'number' && text !== '' ? Number(text) : text);
      }}
    />`
  );
}

const RENDERERS: Record<ScalarKind, (field: ScalarField) => TemplateResult> = {
  toggle: renderToggle,
  textarea: renderTextarea,
  select: renderSelect,
  input: renderInput,
};

/** One scalar action parameter, rendered as the control its definition asks for. */
export function renderScalarParameter(field: ScalarField) {
  return RENDERERS[scalarKind(field.definition)](field);
}
