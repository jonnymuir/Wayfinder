/**
 * Generic, schema-driven property-field renderer for a component's own declared properties
 * (ComponentPropertyDescriptor — see types.ts and
 * docs/guides/extending-the-component-catalog.md). Genuinely recursive: an `Array`-valued
 * property (e.g. a chart's `bands`, a stat-group's `items`) renders a repeatable list, each item
 * dispatching back into this same renderer for its own nested `Object` shape; an `Object`-valued
 * property renders its own nested field group the same way.
 *
 * Deliberately does NOT cover a component's *contained child components*
 * (ComponentContainment — fieldset's children, accordion's sections, radio's
 * conditionalChildren) — that's a materially different, harder problem (see the WCAG risk
 * analysis for phase 6b in the component-catalog-extensibility plan) and is out of scope here.
 * A container type's own flat properties (e.g. fieldset's legend) still go through this renderer
 * like any other type's; only its *children* are excluded.
 */

import { html, nothing, type TemplateResult } from 'lit';
import type { ComponentDescriptor, ComponentPropertyDescriptor } from './types.js';
import type { PropertyReferenceContext } from './component-property-references.js';
import { REGEX_PRESETS } from './regex-presets.js';

export type PropertyPath = Array<string | number>;

/**
 * `PropertyReferenceContext` plus the bits that depend on the *specific component instance*
 * being edited right now, which the generic stage-level context can't know in advance —
 * component-child-editor.ts's `renderComponentNode` resolves these once per component (reading
 * its own live `conditionalOn`/`options` values) before calling into this module.
 */
export interface ResolvedPropertyReferences extends PropertyReferenceContext {
  /** Legal values for VisibleWhen, resolved from whatever ConditionalOn currently points at. */
  conditionalTargetOptions?: string[];
  conditionalTargetKind?: 'options' | 'boolean' | 'text';
  /** This component's own `options` array, for Default on select/radio/checkboxlist. */
  ownOptions?: string[];
}

// ComponentPropertyDescriptor.Key arrives already camelCased to match the real component JSON
// this module reads/writes (e.g. "fieldKey") — server-side, PropertyNameJsonConverter
// (Wayfinder/Models/ServiceDesign/Components/ComponentDescriptor.cs) converts it from the real
// CLR property name at the JSON boundary, so this module can use property.key directly with no
// conversion of its own. Confirmed live against a running host that skipping this step (using
// the raw, un-converted value) silently reads/writes the wrong property: every field in the
// properties panel's edit form appeared blank on open, and typing into one never actually
// reached the field the runtime reads.

function pathKey(path: PropertyPath): string {
  return path.join('-');
}

function getAtPath(value: unknown, path: PropertyPath): unknown {
  return path.reduce<unknown>((current, segment) => {
    if (current === null || current === undefined) {
      return undefined;
    }
    return (current as Record<string | number, unknown>)[segment];
  }, value);
}

/**
 * Immutable set at a nested path within a plain object/array tree — the same "walk and rebuild
 * the spine" shape as route-model.ts's own update helpers, generalised to an arbitrary path
 * instead of one fixed shape.
 */
export function setAtPath<T>(root: T, path: PropertyPath, value: unknown): T {
  if (path.length === 0) {
    return value as T;
  }

  const [head, ...rest] = path;
  if (typeof head === 'number') {
    const array = Array.isArray(root) ? [...root] : [];
    array[head] = setAtPath(array[head], rest, value);
    return array as unknown as T;
  }

  const obj: Record<string, unknown> = root && typeof root === 'object' && !Array.isArray(root)
    ? { ...(root as Record<string, unknown>) }
    : {};
  obj[head] = setAtPath(obj[head], rest, value);
  return obj as unknown as T;
}

function defaultValueFor(property: ComponentPropertyDescriptor): unknown {
  switch (property.valueKind) {
    case 'Boolean':
      return false;
    case 'Integer':
    case 'Number':
      return null;
    case 'StringArray':
    case 'Array':
      return [];
    case 'Object':
      return blankObjectFor(property.properties ?? []);
    default:
      return '';
  }
}

function blankObjectFor(properties: ComponentPropertyDescriptor[]): Record<string, unknown> {
  const obj: Record<string, unknown> = {};
  for (const property of properties) {
    obj[property.key] = defaultValueFor(property);
  }
  return obj;
}

/**
 * A fresh component instance for `descriptor` — every declared property at its default value,
 * PLUS an empty (but structurally present, never absent) child slot when `descriptor.containment`
 * isn't `None`. That slot isn't itself a `Properties` entry (containment is a materially
 * different, out-of-scope-for-this-editor concept — see component-property-editor.ts's own
 * module doc comment), but a brand-new component still needs *some* value there: several
 * existing call sites (describeComponent's fieldset/accordion/summary-list cases, most visibly)
 * read e.g. `.children.length` unconditionally, on the reasonable assumption that a real
 * container component always has one, never `undefined`.
 */
export function blankComponentFor(descriptor: ComponentDescriptor): Record<string, unknown> {
  const base: Record<string, unknown> = { type: descriptor.discriminator, ...blankObjectFor(descriptor.properties) };
  const { containment } = descriptor;

  switch (containment.kind) {
    case 'ChildList':
    case 'NamedSections':
      if (containment.propertyName) {
        base[containment.propertyName] = [];
      }
      break;
    case 'KeyedChildren':
      if (containment.propertyName) {
        base[containment.propertyName] = {};
      }
      break;
    case 'None':
    default:
      break;
  }

  return base;
}

export interface RenderPropertyFieldsOptions {
  value: unknown;
  path?: PropertyPath;
  onChange: (path: PropertyPath, value: unknown) => void;
  idPrefix: string;
  references?: ResolvedPropertyReferences;
}

export function renderComponentPropertyFields(
  properties: ComponentPropertyDescriptor[],
  options: RenderPropertyFieldsOptions
): TemplateResult {
  const { value, path = [], onChange, idPrefix, references } = options;

  return html`
    ${properties.map(property =>
      renderPropertyField(property, getAtPath(value, [property.key]), [...path, property.key], onChange, idPrefix, references)
    )}
  `;
}

function renderPropertyField(
  property: ComponentPropertyDescriptor,
  value: unknown,
  path: PropertyPath,
  onChange: (path: PropertyPath, value: unknown) => void,
  idPrefix: string,
  references?: ResolvedPropertyReferences
): TemplateResult {
  const fieldId = `${idPrefix}-${pathKey(path)}`;

  if (property.valueKind === 'Array') {
    const items = Array.isArray(value) ? value : [];
    const itemProperties = property.items?.properties;
    const itemLabel = property.items?.title ?? (property.title.replace(/s$/i, '') || 'item');

    return html`
      <div class="field-block field-block-full property-array">
        <span class="field-label" id="${fieldId}-legend">${property.title}${property.required ? ' *' : ''}</span>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
        <ul class="property-array-list" aria-labelledby="${fieldId}-legend">
          ${items.map((item, index) => html`
            <li class="property-array-item" data-wayfinder-property-array-item="${fieldId}-${index}">
              <div class="property-array-item-fields">
                ${itemProperties
                  ? renderComponentPropertyFields(itemProperties, {
                      value: item,
                      path: [...path, index],
                      onChange,
                      idPrefix,
                      references,
                    })
                  : renderScalarLikeField(
                      property.items ?? { key: 'value', title: itemLabel, valueKind: 'String', required: false },
                      item,
                      [...path, index],
                      onChange,
                      `${fieldId}-${index}`,
                      references
                    )}
              </div>
              <button
                type="button"
                class="text-button property-array-remove"
                aria-label="Remove ${itemLabel} ${index + 1} from ${property.title}"
                @click=${() => onChange(path, items.filter((_, i) => i !== index))}
              >Remove</button>
            </li>
          `)}
        </ul>
        <button
          type="button"
          class="secondary-button"
          aria-label="Add ${itemLabel} to ${property.title}"
          @click=${() => onChange(path, [...items, itemProperties ? blankObjectFor(itemProperties) : defaultValueFor(property.items ?? property)])}
        >+ Add ${itemLabel}</button>
      </div>
    `;
  }

  if (property.valueKind === 'Object') {
    return html`
      <fieldset class="field-block field-block-full property-object">
        <legend class="field-label">${property.title}${property.required ? ' *' : ''}</legend>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
        ${property.properties
          ? renderComponentPropertyFields(property.properties, { value, path, onChange, idPrefix, references })
          : nothing}
      </fieldset>
    `;
  }

  return renderScalarLikeField(property, value, path, onChange, fieldId, references);
}

/**
 * Resolves a `Format`-tagged property's legal-value list against the live reference context —
 * `undefined` means "not a reference-aware format, or the context needed to resolve it wasn't
 * supplied," which falls back to the plain text/number input below; an empty array is a genuine
 * "no candidates yet" (e.g. this is the first field in the stage) and still renders as a select
 * with only "-- Not set --", which is more honest than a text box that can't be filled correctly
 * either way.
 */
function referenceSelectOptions(
  property: ComponentPropertyDescriptor,
  references: ResolvedPropertyReferences | undefined
): Array<{ value: string; label: string }> | undefined {
  if (!references) {
    return undefined;
  }

  switch (property.format) {
    case 'field-ref':
      return references.siblingFields.map(field => ({ value: field.fieldKey, label: `${field.label} (${field.fieldKey})` }));
    case 'conditional-value-ref':
      if (references.conditionalTargetKind === 'boolean') {
        return [{ value: 'true', label: 'true' }, { value: 'false', label: 'false' }];
      }
      if (references.conditionalTargetKind === 'options' && references.conditionalTargetOptions) {
        return references.conditionalTargetOptions.map(option => ({ value: option, label: option }));
      }
      return undefined;
    case 'own-options-ref':
      return (references.ownOptions ?? []).map(option => ({ value: option, label: option }));
    case 'calculation-ref':
      return references.calculationFieldNames.map(name => ({ value: name, label: name }));
    case 'stage-ref':
      return references.stageOptions.map(stage => ({ value: stage.key, label: stage.label }));
    case 'field-or-calc-ref':
      return [
        ...references.allFields.map(field => ({ value: field.fieldKey, label: `${field.label} (${field.fieldKey})` })),
        ...references.calculationFieldNames.map(name => ({ value: name, label: `${name} (calculation)` })),
      ];
    default:
      return undefined;
  }
}

/**
 * The regex text input, unchanged, plus two design-time-only affordances: a preset-insert
 * `<select>` (writes a chosen preset's pattern through the normal `onChange`, still hand-editable
 * afterwards) and a live "test a sample value" input. The tester's result is NOT part of the
 * component's own saved value — it's scratch-only — so it's a plain DOM `@input` handler that
 * writes straight into a sibling `<span>` by id, rather than adding new Lit reactive state to
 * this otherwise-stateless render module.
 */
function renderPatternField(
  property: ComponentPropertyDescriptor,
  value: unknown,
  path: PropertyPath,
  onChange: (path: PropertyPath, value: unknown) => void,
  fieldId: string
): TemplateResult {
  const presetId = `${fieldId}-preset`;
  const testerId = `${fieldId}-tester`;
  const testerResultId = `${fieldId}-tester-result`;

  return html`
    <div class="field-block pattern-field">
      <label class="field-block" for=${fieldId}>
        <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
        <input
          id=${fieldId}
          class="field-control"
          type="text"
          ?required=${property.required}
          .value=${value === undefined || value === null ? '' : String(value)}
          @input=${(event: Event) => onChange(path, (event.currentTarget as HTMLInputElement).value)}
        />
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </label>
      <label class="field-block" for=${presetId}>
        <span class="field-label">Insert a common pattern</span>
        <select
          id=${presetId}
          class="field-control"
          @change=${(event: Event) => {
            const select = event.currentTarget as HTMLSelectElement;
            if (select.value) {
              onChange(path, select.value);
            }
            select.value = '';
          }}
        >
          <option value="">-- Choose a preset --</option>
          ${REGEX_PRESETS.map(preset => html`<option value=${preset.pattern}>${preset.label}</option>`)}
        </select>
      </label>
      <label class="field-block" for=${testerId}>
        <span class="field-label">Test a sample value</span>
        <input
          id=${testerId}
          class="field-control"
          type="text"
          placeholder="Type a sample value to test against the pattern above"
          @input=${(event: Event) => {
            const sample = (event.currentTarget as HTMLInputElement).value;
            // getElementById on `document` can't reach elements inside a shadow root — this
            // whole tree renders inside wayfinder-step-inspector's (possibly nested) shadow DOM,
            // so look up from the input's own root node instead.
            const root = (event.currentTarget as HTMLElement).getRootNode() as Document | ShadowRoot;
            const resultEl = root.getElementById(testerResultId);
            if (!resultEl) {
              return;
            }
            const currentPattern = root.getElementById(fieldId) as HTMLInputElement | null;
            const pattern = currentPattern?.value ?? '';
            if (sample === '' || pattern === '') {
              resultEl.textContent = '';
              resultEl.className = 'field-help';
              return;
            }
            try {
              const matches = new RegExp(pattern).test(sample);
              resultEl.textContent = matches ? 'Matches the pattern.' : 'Does not match the pattern.';
              resultEl.className = matches ? 'field-help pattern-tester-pass' : 'field-help pattern-tester-fail';
            } catch {
              resultEl.textContent = 'Invalid pattern — this is not a valid regular expression.';
              resultEl.className = 'field-help pattern-tester-fail';
            }
          }}
        />
        <span id=${testerResultId} class="field-help" data-wayfinder-pattern-tester-result></span>
      </label>
    </div>
  `;
}

function renderScalarLikeField(
  property: ComponentPropertyDescriptor,
  value: unknown,
  path: PropertyPath,
  onChange: (path: PropertyPath, value: unknown) => void,
  fieldId: string,
  references?: ResolvedPropertyReferences
): TemplateResult {
  const editor = property.editor ?? (property.allowedValues?.length ? 'select' : undefined);
  const referenceOptions = referenceSelectOptions(property, references);

  if (property.format === 'pattern') {
    return renderPatternField(property, value, path, onChange, fieldId);
  }

  if (referenceOptions) {
    return html`
      <label class="field-block" for=${fieldId}>
        <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
        <select
          id=${fieldId}
          class="field-control"
          ?required=${property.required}
          @change=${(event: Event) => onChange(path, (event.currentTarget as HTMLSelectElement).value)}
        >
          ${!property.required ? html`<option value="" ?selected=${!value}>-- Not set --</option>` : nothing}
          ${referenceOptions.map(option => html`
            <option value=${option.value} ?selected=${String(value ?? '') === option.value}>${option.label}</option>
          `)}
        </select>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </label>
    `;
  }

  if (editor === 'toggle' || property.valueKind === 'Boolean') {
    // Wrapped in .field-block like every other field type below — a bare <label
    // class="field-toggle"> plus a sibling .field-help span would otherwise be two separate
    // top-level grid items in the parent .field-grid, not one field, leaving the help text with
    // no visual grouping with its own toggle and only the *next* field's own top margin (if any)
    // separating it from the following field's label — confirmed live: this produced almost no
    // visible gap at all between a toggle's help text and the next field's heading.
    return html`
      <div class="field-block">
        <label class="field-toggle">
          <input
            type="checkbox"
            id=${fieldId}
            .checked=${Boolean(value)}
            @change=${(event: Event) => onChange(path, (event.currentTarget as HTMLInputElement).checked)}
          />
          <span>${property.title}${property.required ? ' *' : ''}</span>
        </label>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </div>
    `;
  }

  if (editor === 'textarea') {
    return html`
      <label class="field-block" for=${fieldId}>
        <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
        <textarea
          id=${fieldId}
          class="field-control field-textarea"
          ?required=${property.required}
          .value=${typeof value === 'string' ? value : value == null ? '' : String(value)}
          @input=${(event: Event) => onChange(path, (event.currentTarget as HTMLTextAreaElement).value)}
        ></textarea>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </label>
    `;
  }

  if (editor === 'select' || property.allowedValues?.length) {
    return html`
      <label class="field-block" for=${fieldId}>
        <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
        <select
          id=${fieldId}
          class="field-control"
          ?required=${property.required}
          @change=${(event: Event) => onChange(path, (event.currentTarget as HTMLSelectElement).value)}
        >
          ${!property.required ? html`<option value="" ?selected=${!value}>-- Not set --</option>` : nothing}
          ${property.allowedValues?.map(option => html`
            <option value=${option} ?selected=${String(value ?? '') === option}>${option}</option>
          `)}
        </select>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </label>
    `;
  }

  if (property.valueKind === 'StringArray') {
    const items = Array.isArray(value) ? (value as unknown[]).map(String) : [];
    return html`
      <label class="field-block field-block-full" for=${fieldId}>
        <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
        <textarea
          id=${fieldId}
          class="field-control field-textarea"
          placeholder="One value per line"
          .value=${items.join('\n')}
          @change=${(event: Event) => {
            const next = (event.currentTarget as HTMLTextAreaElement).value
              .split('\n')
              .map(line => line.trim())
              .filter(line => line.length > 0);
            onChange(path, next);
          }}
        ></textarea>
        ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
      </label>
    `;
  }

  const inputType =
    property.editor === 'date' || property.format === 'date'
      ? 'date'
      : property.editor === 'color' || property.format === 'color'
        ? 'color'
        : property.valueKind === 'Integer' || property.valueKind === 'Number'
          ? 'number'
          : 'text';

  return html`
    <label class="field-block" for=${fieldId}>
      <span class="field-label">${property.title}${property.required ? ' *' : ''}</span>
      <input
        id=${fieldId}
        class="field-control"
        type=${inputType}
        ?required=${property.required}
        pattern=${property.pattern ?? nothing}
        minlength=${property.minLength ?? nothing}
        maxlength=${property.maxLength ?? nothing}
        min=${property.minimum ?? nothing}
        max=${property.maximum ?? nothing}
        .value=${value === undefined || value === null ? '' : String(value)}
        @input=${(event: Event) => {
          const raw = (event.currentTarget as HTMLInputElement).value;
          onChange(path, inputType === 'number' ? (raw === '' ? null : Number(raw)) : raw);
        }}
      />
      ${property.description ? html`<span class="field-help">${property.description}</span>` : nothing}
    </label>
  `;
}
