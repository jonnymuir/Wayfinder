/**
 * Phase 6b — recursive editing of a component's own *contained child components*
 * (ComponentContainment: fieldset's children, accordion's sections, radio/checkboxlist's
 * conditionalChildren), on top of phase 6a's flat property editor
 * (component-property-editor.ts), which deliberately stopped short of this.
 *
 * Uses native, uncontrolled `<details>/<summary>` for every expand/collapse point — no ARIA
 * `role="tree"` widget, no hand-rolled keyboard handling: a `<details>` is natively keyboard-
 * operable (Enter/Space on its `<summary>`) and, critically, needs no component-level state to
 * track *which* of a potentially deep, dynamically-changing set of nested containers is open —
 * the browser already tracks that per-element, surviving a Lit re-render as long as the DOM node
 * itself isn't recreated (which it isn't; re-renders only touch attributes/children in place).
 *
 * Two of this codebase's genuinely new WCAG risks (see the component-catalog-extensibility
 * plan's phase-6b risk analysis) are handled explicitly here, not left as theoretical:
 * - Deleting a node whose subtree contains the current focus would otherwise lose focus to
 *   `<body>` — every delete call explicitly refocuses a stable, always-present control (the
 *   surviving parent container's own "+ Add component" button) via `onFocusContainer`.
 * - Reordering uses visible, always-keyboard-operable Up/Down buttons (Enter/Space activates
 *   any button) rather than a keyboard-only Alt+Arrow shortcut with no visible affordance — a
 *   deliberate, arguably more discoverable alternative to the plan's suggested pattern, not an
 *   oversight.
 *
 * Explicitly NOT covered, by design, to keep this reviewable:
 * - Moving a child *between* two different containment slots (e.g. from one radio option's
 *   conditional children to another's) — delete-then-re-add is the only way today. The plan
 *   flags a "move to…" menu as the right non-drag-and-drop mechanism; building that is future
 *   work, not silently missing.
 * - Auto-expanding an ancestor `<details>` around a validation error — there is no live,
 *   descriptor-driven validation error surfaced in this properties-panel UI yet (phase 6a
 *   didn't wire this in either); once one exists, this is where it would need to hook in.
 */

import { html, nothing, type TemplateResult } from 'lit';
import type { AuthoredComponent, ComponentDescriptor } from './types.js';
import { blankComponentFor, renderComponentPropertyFields, type PropertyPath } from './component-property-editor.js';

export interface ChildEditorContext {
  /** Every registered component type — a child's own descriptor is looked up here by discriminator. */
  catalog: ComponentDescriptor[];
  onChange: (path: PropertyPath, value: unknown) => void;
  onAnnounce: (message: string) => void;
  /** Refocuses the "+ Add component" control of the child-list container at this path, after a delete. */
  onFocusContainer: (containerPath: PropertyPath) => void;
  idPrefix: string;
}

function lowerFirst(text: string): string {
  return text.length === 0 ? text : text.charAt(0).toLowerCase() + text.slice(1);
}

function describeChildLabel(component: AuthoredComponent): string {
  return (component as { label?: string }).label
    ?? (component as { fieldKey?: string }).fieldKey
    ?? (component as { legend?: string | null }).legend
    ?? component.type;
}

/**
 * Renders one component's own property fields (phase 6a) plus, if it's a container, its
 * children (phase 6b) — the single recursive unit reused at every depth, so a fieldset
 * containing another fieldset works with no special-casing.
 */
export function renderComponentNode(
  component: AuthoredComponent,
  path: PropertyPath,
  ctx: ChildEditorContext
): TemplateResult {
  const descriptor = ctx.catalog.find(candidate => candidate.discriminator === component.type);
  if (!descriptor) {
    return html`<p class="section-empty">Unknown component type "${component.type}" — edit it via the Definition tab.</p>`;
  }

  const idPrefix = `${ctx.idPrefix}-${path.join('-')}`;

  return html`
    ${renderComponentPropertyFields(descriptor.properties, { value: component, path, onChange: ctx.onChange, idPrefix })}
    ${renderContainment(component, descriptor, path, ctx)}
  `;
}

function renderContainment(
  component: AuthoredComponent,
  descriptor: ComponentDescriptor,
  path: PropertyPath,
  ctx: ChildEditorContext
): TemplateResult {
  const { containment } = descriptor;
  if (containment.kind === 'None' || !containment.propertyName) {
    return html``;
  }

  const record = component as unknown as Record<string, unknown>;
  const propertyKey = lowerFirst(containment.propertyName);

  if (containment.kind === 'ChildList') {
    const children = Array.isArray(record[propertyKey]) ? (record[propertyKey] as AuthoredComponent[]) : [];
    return renderChildList(children, [...path, propertyKey], ctx, containment.propertyName);
  }

  if (containment.kind === 'NamedSections') {
    const sections = Array.isArray(record[propertyKey])
      ? (record[propertyKey] as Array<{ heading?: string; summary?: string | null; children?: AuthoredComponent[] }>)
      : [];
    const sectionChildrenKey = containment.sectionChildrenPropertyName
      ? lowerFirst(containment.sectionChildrenPropertyName)
      : 'children';
    const sectionsPath: PropertyPath = [...path, propertyKey];
    const containerKey = sectionsPath.join('-');

    return html`
      <details class="child-container" open data-wayfinder-child-container="${containerKey}">
        <summary>${containment.propertyName} (${sections.length})</summary>
        ${sections.map((section, sectionIndex) => html`
          <div class="child-section">
            <label class="field-block">
              <span class="field-label">Heading</span>
              <input
                class="field-control"
                .value=${section.heading ?? ''}
                @input=${(event: Event) =>
                  ctx.onChange([...sectionsPath, sectionIndex, 'heading'], (event.currentTarget as HTMLInputElement).value)}
              />
            </label>
            ${renderChildList(
              section.children ?? [],
              [...sectionsPath, sectionIndex, sectionChildrenKey],
              ctx,
              `${section.heading || `Section ${sectionIndex + 1}`} children`
            )}
          </div>
        `)}
        <button
          type="button"
          class="secondary-button"
          @click=${() => {
            const next = [...sections, { heading: 'New section', children: [] }];
            ctx.onChange(sectionsPath, next);
            ctx.onAnnounce('Section added.');
          }}
        >+ Add section</button>
      </details>
    `;
  }

  // KeyedChildren — radio/checkboxlist's conditionalChildren, keyed by a subset of a sibling
  // Options property (see ComponentContainment.keySourceProperty).
  if (containment.kind === 'KeyedChildren' && containment.keySourceProperty) {
    const optionsKey = lowerFirst(containment.keySourceProperty);
    const options = Array.isArray(record[optionsKey]) ? (record[optionsKey] as string[]) : [];
    const byKey = (record[propertyKey] && typeof record[propertyKey] === 'object')
      ? (record[propertyKey] as Record<string, AuthoredComponent[]>)
      : {};
    const keyedPath: PropertyPath = [...path, propertyKey];
    const containerKey = keyedPath.join('-');

    if (options.length === 0) {
      return html`<p class="section-empty">Add options above to enable conditional children.</p>`;
    }

    return html`
      <details class="child-container" open data-wayfinder-child-container="${containerKey}">
        <summary>${containment.propertyName} (${Object.keys(byKey).length} of ${options.length} option${options.length === 1 ? '' : 's'})</summary>
        ${options.map(option => html`
          <div class="child-section">
            <p class="field-label">When "${option}" is selected</p>
            ${renderChildList(byKey[option] ?? [], [...keyedPath, option], ctx, `"${option}" children`)}
          </div>
        `)}
      </details>
    `;
  }

  return html``;
}

function renderChildList(
  children: AuthoredComponent[],
  childrenPath: PropertyPath,
  ctx: ChildEditorContext,
  label: string
): TemplateResult {
  const containerKey = childrenPath.join('-');

  return html`
    <details class="child-container" open data-wayfinder-child-container="${containerKey}">
      <summary>${label} (${children.length})</summary>
      ${children.length > 0
        ? html`
            <ul class="field-list child-list">
              ${children.map((child, index) => html`
                <li class="field-item component-item child-item">
                  <details class="child-editor">
                    <summary class="component-item-header">
                      <span class="field-item-label">${describeChildLabel(child)}</span>
                      <span class="field-item-meta">${child.type}</span>
                    </summary>
                    <div class="component-editor">
                      ${renderComponentNode(child, [...childrenPath, index], ctx)}
                    </div>
                  </details>
                  <div class="component-item-actions">
                    <button
                      type="button"
                      class="icon-button"
                      ?disabled=${index === 0}
                      aria-label="Move ${describeChildLabel(child)} up within ${label}"
                      @click=${() => {
                        const next = [...children];
                        [next[index - 1], next[index]] = [next[index], next[index - 1]];
                        ctx.onChange(childrenPath, next);
                      }}
                    >↑<span class="sr-only"> Move up</span></button>
                    <button
                      type="button"
                      class="icon-button"
                      ?disabled=${index === children.length - 1}
                      aria-label="Move ${describeChildLabel(child)} down within ${label}"
                      @click=${() => {
                        const next = [...children];
                        [next[index], next[index + 1]] = [next[index + 1], next[index]];
                        ctx.onChange(childrenPath, next);
                      }}
                    >↓<span class="sr-only"> Move down</span></button>
                    <button
                      type="button"
                      class="icon-button danger-button"
                      aria-label="Delete ${describeChildLabel(child)} from ${label}"
                      @click=${() => {
                        const next = children.filter((_, i) => i !== index);
                        ctx.onChange(childrenPath, next);
                        ctx.onAnnounce(`${describeChildLabel(child)} deleted from ${label}.`);
                        ctx.onFocusContainer(childrenPath);
                      }}
                    >Delete</button>
                  </div>
                </li>
              `)}
            </ul>
          `
        : nothing}
      <div class="component-add-row">
        <label class="sr-only" for="add-child-type-${containerKey}">Component type to add to ${label}</label>
        <select id="add-child-type-${containerKey}" class="field-control" data-wayfinder-add-child-type>
          ${ctx.catalog.map(descriptor => html`
            <option value=${descriptor.discriminator}>${descriptor.displayName}</option>
          `)}
        </select>
        <button
          type="button"
          class="secondary-button"
          aria-label="Add component to ${label}"
          @click=${(event: Event) => {
            const row = (event.currentTarget as HTMLElement).closest('.component-add-row');
            const select = row?.querySelector<HTMLSelectElement>('[data-wayfinder-add-child-type]');
            const descriptor = ctx.catalog.find(candidate => candidate.discriminator === select?.value);
            if (!descriptor) {
              ctx.onAnnounce('Choose a component type before adding.');
              return;
            }

            const next = [...children, blankComponentFor(descriptor) as unknown as AuthoredComponent];
            ctx.onChange(childrenPath, next);
            ctx.onAnnounce(`${descriptor.displayName} added to ${label}.`);
          }}
        >+ Add component</button>
      </div>
    </details>
  `;
}
