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

export type PropertyPath = Array<string | number>;

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

function lowerFirst(text: string): string {
  return text.length === 0 ? text : text.charAt(0).toLowerCase() + text.slice(1);
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
        base[lowerFirst(containment.propertyName)] = [];
      }
      break;
    case 'KeyedChildren':
      if (containment.propertyName) {
        base[lowerFirst(containment.propertyName)] = {};
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
}

export function renderComponentPropertyFields(
  properties: ComponentPropertyDescriptor[],
  options: RenderPropertyFieldsOptions
): TemplateResult {
  const { value, path = [], onChange, idPrefix } = options;

  return html`
    ${properties.map(property =>
      renderPropertyField(property, getAtPath(value, [property.key]), [...path, property.key], onChange, idPrefix)
    )}
  `;
}

function renderPropertyField(
  property: ComponentPropertyDescriptor,
  value: unknown,
  path: PropertyPath,
  onChange: (path: PropertyPath, value: unknown) => void,
  idPrefix: string
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
                    })
                  : renderScalarLikeField(
                      property.items ?? { key: 'value', title: itemLabel, valueKind: 'String', required: false },
                      item,
                      [...path, index],
                      onChange,
                      `${fieldId}-${index}`
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
          ? renderComponentPropertyFields(property.properties, { value, path, onChange, idPrefix })
          : nothing}
      </fieldset>
    `;
  }

  return renderScalarLikeField(property, value, path, onChange, fieldId);
}

function renderScalarLikeField(
  property: ComponentPropertyDescriptor,
  value: unknown,
  path: PropertyPath,
  onChange: (path: PropertyPath, value: unknown) => void,
  fieldId: string
): TemplateResult {
  const editor = property.editor ?? (property.allowedValues?.length ? 'select' : undefined);

  if (editor === 'toggle' || property.valueKind === 'Boolean') {
    return html`
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
