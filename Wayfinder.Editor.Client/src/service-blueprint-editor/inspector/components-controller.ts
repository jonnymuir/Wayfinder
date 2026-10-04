import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { Component, StageDefinition } from '../types.js';
import { blankComponentFor, setAtPath, type PropertyPath } from '../component-property-editor.js';
import { renderComponentNode } from '../component-child-editor.js';
import { buildPropertyReferenceContext } from '../component-property-references.js';
import type { InspectorHost } from './inspector-host.js';

function describeComponent(component: Component): string {
  switch (component.type) {
    case 'fieldset':
      return component.legend
        ? `${component.legend} · ${component.children.length} item${component.children.length === 1 ? '' : 's'}`
        : `Fieldset · ${component.children.length} item${component.children.length === 1 ? '' : 's'}`;
    case 'accordion':
      return `Accordion · ${component.sections.length} section${component.sections.length === 1 ? '' : 's'}`;
    case 'panel':
      return component.heading;
    case 'waiting':
      return component.content;
    case 'summary-list':
      return `Summary list · ${component.children.length} row${component.children.length === 1 ? '' : 's'}`;
    case 'task-list': {
      const taskCount = (component.sections ?? []).reduce((sum, section) => sum + section.tasks.length, 0);
      return `Task list · ${taskCount} task${taskCount === 1 ? '' : 's'}`;
    }
    case 'body':
    case 'inset-text':
    case 'warning-text':
    case 'details':
    case 'heading':
    case 'notification-banner':
      return (
        ('content' in component ? component.content : undefined) ??
        ('heading' in component ? component.heading : undefined) ??
        component.type
      );
    case 'stat-group':
      return `${component.title ?? 'Statistics'} · ${component.items.length} tile${component.items.length === 1 ? '' : 's'}`;
    case 'chart':
      return `${component.title ?? 'Chart'} · bound to ${component.series}`;
    default:
      // Every remaining type — the full input catalog (text/number/decimal/select/radio/
      // checkboxlist/date/email/textarea/boolean/slider/file-upload/guidance-checklist) — shares
      // `label`/`fieldKey` via AuthoredInputComponentBase, so this stays generic rather than
      // needing a case added every time a new input type is registered.
      return (component as { label?: string }).label ?? (component as { fieldKey?: string }).fieldKey ?? component.type;
  }
}
/** The components section of the stage panel: the list, the add control, and each component's editor. */
export class ComponentsController implements ReactiveController {
  private _expandedIndexValue: number | null = null;
  private _lastStageKey: string | null = null;

  /** Index of a just-added component so it can be expanded and its first field focused once rendered. */
  private _newlyAddedIndex: number | null = null;

  constructor(
    private readonly _host: InspectorHost,
    private readonly _setComponents: (components: Component[]) => void
  ) {
    _host.addController(this);
  }

  private get _expandedComponentIndex() {
    return this._expandedIndexValue;
  }

  private set _expandedComponentIndex(value: number | null) {
    this._expandedIndexValue = value;
    this._host.requestUpdate();
  }

  /** The expanded component belongs to the stage it was expanded on. */
  hostUpdate() {
    const stageKey = this._host.selectedStage?.stageKey ?? null;
    if (stageKey !== this._lastStageKey) {
      this._lastStageKey = stageKey;
      this._expandedIndexValue = null;
    }
  }

  hostUpdated() {
    if (this._newlyAddedIndex === null) {
      return;
    }
    const index = this._newlyAddedIndex;
    this._newlyAddedIndex = null;
    requestAnimationFrame(() => {
      const container = this._host.shadowRoot?.querySelector<HTMLElement>(`[data-wayfinder-component-index="${index}"]`);
      container?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      container?.querySelector<HTMLElement>('input, select, textarea')?.focus();
    });
  }

  renderSection(stage: StageDefinition) {
    const components = stage.components ?? [];
    return html`
        <section class="inspector-section" aria-labelledby="stage-components-heading">
          <div class="section-header-row">
            <h3 id="stage-components-heading" class="section-heading">Components</h3>
            <span class="section-meta">${components.length}</span>
          </div>
          ${
            components.length === 0
              ? html`<p class="section-empty">No components defined for this stage.</p>`
              : html`
                <ul class="field-list" data-wayfinder-stage-components>
                  ${components.map((component, index) => this._renderComponentListItem(component, index))}
                </ul>
              `
          }
          ${
            this._host.componentCatalog.length > 0
              ? html`
                <div class="component-add-row">
                  <label class="sr-only" for="add-component-type-${stage.stageKey}">Component type to add</label>
                  <select
                    id="add-component-type-${stage.stageKey}"
                    class="field-control"
                    data-wayfinder-add-component-type
                  >
                    ${this._host.componentCatalog.map(
                      (descriptor) => html`
                      <option value=${descriptor.discriminator}>${descriptor.displayName}</option>
                    `
                    )}
                  </select>
                  <button
                    type="button"
                    class="secondary-button"
                    aria-label="Add component to ${stage.displayName}"
                    @click=${this._handleAddComponent}
                  >+ Add component</button>
                </div>
              `
              : html`
                <p class="section-empty">
                  To add components, switch to the <strong>Definition</strong> tab and edit this stage's
                  <code>components</code> block in the JSON editor.
                </p>
              `
          }
        </section>
    `;
  }

  private readonly _handleAddComponent = () => {
    const stage = this._host.selectedStage;
    if (!stage || this._host.componentCatalog.length === 0) {
      return;
    }

    const select = this._host.shadowRoot?.querySelector<HTMLSelectElement>('[data-wayfinder-add-component-type]');
    const descriptor = this._host.componentCatalog.find((candidate) => candidate.discriminator === select?.value);
    if (!descriptor) {
      this._host.announce('Choose a component type before adding.');
      return;
    }

    const nextComponent = blankComponentFor(descriptor) as unknown as Component;
    const components = [...(stage.components ?? []), nextComponent];
    const newIndex = components.length - 1;

    this._setComponents(components);
    this._expandedComponentIndex = newIndex;
    this._newlyAddedIndex = newIndex;
    this._host.announce(`${descriptor.displayName} component added.`);
  };

  /**
   * `path` is rooted at the stage's own `components` array (e.g. `[0, 'children', 2, 'label']`
   * addresses the 3rd child of the 1st component's ChildList) — the phase 6b recursive
   * container-children editor (component-child-editor.ts) reaches arbitrarily deep components
   * the same way phase 6a reached a single component's own flat properties, via the same
   * `setAtPath` utility.
   */
  private _handleComponentTreeChange(path: PropertyPath, value: unknown) {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const components = stage.components ?? [];
    const nextComponents = setAtPath(components, path, value);
    this._setComponents(nextComponents);
  }

  /**
   * Refocuses the "+ Add component" control of the child-list container at `containerPath` —
   * called after a nested child delete, so focus never falls through to `<body>` when the
   * deleted subtree contained it. The container itself is always structurally present (it
   * renders its own "+ Add component" row even with zero children), so this reliably finds a
   * surviving target; the top-level "+ Add component" control is the final fallback.
   */
  private _focusChildContainer(containerPath: PropertyPath) {
    const key = containerPath.join('-');
    requestAnimationFrame(() => {
      const container = this._host.shadowRoot?.querySelector<HTMLElement>(`[data-wayfinder-child-container="${key}"]`);
      const addButton = container?.querySelector<HTMLElement>('.component-add-row .secondary-button');
      (addButton ?? this._host.shadowRoot?.querySelector<HTMLElement>('[data-wayfinder-add-component-type]'))?.focus();
    });
  }

  private _handleDeleteComponent(index: number) {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const components = stage.components ?? [];
    const component = components[index];
    if (!component) {
      return;
    }

    const nextComponents = components.filter((_, i) => i !== index);
    this._expandedComponentIndex = null;
    this._setComponents(nextComponents);
    this._host.announce(`${describeComponent(component)} component deleted.`);
    // The deleted item's own controls no longer exist to refocus — the "+ Add component"
    // control is the nearest stable, always-present target, matching the same "refocus a
    // surviving ancestor's own control, not <body>" pattern used elsewhere in this file.
    requestAnimationFrame(() => {
      this._host.shadowRoot?.querySelector<HTMLElement>('[data-wayfinder-add-component-type]')?.focus();
    });
  }

  private _toggleComponentExpanded(index: number) {
    this._expandedComponentIndex = this._expandedComponentIndex === index ? null : index;
  }

  private _renderComponentListItem(component: Component, index: number) {
    const descriptor = this._host.componentCatalog.find((candidate) => candidate.discriminator === component.type);
    const expanded = this._expandedComponentIndex === index;
    const label = describeComponent(component);
    const editorId = `component-editor-${index}`;

    return html`
      <li class="field-item component-item" data-wayfinder-component-index="${index}">
        <div class="component-item-header">
          <span class="field-item-label">${label}</span>
          <span class="field-item-meta">${component.type}</span>
          <div class="component-item-actions">
            ${
              descriptor
                ? html`
                  <button
                    type="button"
                    class="secondary-button"
                    aria-expanded=${String(expanded)}
                    aria-controls=${editorId}
                    @click=${() => this._toggleComponentExpanded(index)}
                  >${expanded ? 'Close' : 'Edit'}</button>
                `
                : nothing
            }
            <button
              type="button"
              class="icon-button danger-button"
              aria-label="Delete ${label} component"
              @click=${() => this._handleDeleteComponent(index)}
            >Delete</button>
          </div>
        </div>
        ${expanded && descriptor ? this._renderComponentEditor(component, index, editorId) : nothing}
      </li>
    `;
  }

  private _renderComponentEditor(component: Component, index: number, editorId: string) {
    const references = buildPropertyReferenceContext(
      this._host.serviceBlueprint,
      this._host.selectedStage?.components,
      this._host.componentCatalog
    );

    return html`
      <div id=${editorId} class="component-editor field-grid">
        ${renderComponentNode(component, [index], {
          catalog: this._host.componentCatalog,
          onChange: (path, value) => this._handleComponentTreeChange(path, value),
          onAnnounce: (message) => this._host.announce(message),
          onFocusContainer: (containerPath) => this._focusChildContainer(containerPath),
          idPrefix: `component-${index}`,
          references,
        })}
      </div>
    `;
  }
}
