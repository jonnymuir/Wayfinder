import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { Component, StageDefinition, StageKind, ServiceBlueprint } from '../types.js';
import { serviceBlueprintGateways, serviceBlueprintStages } from '../types.js';
import { defaultIconForStage, type NodeIconName } from '../graph/node-icons.js';
import { blankComponentFor, setAtPath, type PropertyPath } from '../component-property-editor.js';
import { renderComponentNode } from '../component-child-editor.js';
import { buildPropertyReferenceContext } from '../component-property-references.js';
import { applyQueueToStage, stageQueueKey, stageQueueLabel, serviceBlueprintQueueOptions } from '../stage-assignment.js';
import {
  isTerminalStage,
  serviceBlueprintDeadEndStages,
  serviceBlueprintOrphanedStages,
  serviceBlueprintOutgoingRoutes,
  serviceBlueprintUnreachableStages,
} from '../service-blueprint-validation.js';
import { STAGE_KIND_OPTIONS, stageKindLabel } from '../stage-kind-options.js';
import type { ActionSelectedDetail, ActionsUpdatedDetail, InspectorHost } from './inspector-host.js';
import type { RouteEditorController } from './route-editor-controller.js';
import { renderIconPicker } from './node-icon-picker.js';

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

/** The properties panel for a selected stage: identity, queue, type, actions, components and its routes. */
export class StageInspectorController implements ReactiveController {
  private _stageKeyErrorValue: string | null = null;
  private _expandedComponentIndexValue: number | null = null;
  private _lastStageKey: string | null = null;

  /** Index of a just-added component so it can be expanded and its first field focused once rendered. */
  private _newlyAddedComponentIndex: number | null = null;

  constructor(
    private readonly _host: InspectorHost,
    private readonly _routes: RouteEditorController
  ) {
    _host.addController(this);
  }

  private get _stageKeyError() {
    return this._stageKeyErrorValue;
  }

  private set _stageKeyError(value: string | null) {
    this._stageKeyErrorValue = value;
    this._host.requestUpdate();
  }

  private get _expandedComponentIndex() {
    return this._expandedComponentIndexValue;
  }

  private set _expandedComponentIndex(value: number | null) {
    this._expandedComponentIndexValue = value;
    this._host.requestUpdate();
  }

  /** The key error and expanded component belong to the stage they were raised on. */
  hostUpdate() {
    const stageKey = this._host.selectedStage?.stageKey ?? null;
    if (stageKey !== this._lastStageKey) {
      this._lastStageKey = stageKey;
      this._stageKeyErrorValue = null;
      this._expandedComponentIndexValue = null;
    }
  }

  hostUpdated() {
    if (this._newlyAddedComponentIndex === null) {
      return;
    }
    const index = this._newlyAddedComponentIndex;
    this._newlyAddedComponentIndex = null;
    requestAnimationFrame(() => {
      const container = this._host.shadowRoot?.querySelector<HTMLElement>(`[data-wayfinder-component-index="${index}"]`);
      container?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      container?.querySelector<HTMLElement>('input, select, textarea')?.focus();
    });
  }

  private readonly _handleActionSelected = (event: CustomEvent<ActionSelectedDetail>) => {
    event.stopPropagation();
    this._host.dispatchEvent(
      new CustomEvent<ActionSelectedDetail>('action-selected', {
        detail: event.detail,
        bubbles: true,
        composed: true,
      })
    );
  };

  private _selectedStageOutgoing(stage: StageDefinition) {
    return this._host.serviceBlueprint ? serviceBlueprintOutgoingRoutes(this._host.serviceBlueprint, stage.stageKey) : [];
  }

  private _replaceSelectedStage(nextStage: StageDefinition, previousStageKey = this._host.selectedStage?.stageKey) {
    if (!this._host.serviceBlueprint || !previousStageKey) {
      return;
    }

    const stageIndex = this._host.serviceBlueprint.stages.findIndex((stage) => stage.stageKey === previousStageKey);
    if (stageIndex < 0) {
      return;
    }

    let gateways = serviceBlueprintGateways(this._host.serviceBlueprint);
    let initialStageKey = this._host.serviceBlueprint.initialStage;
    let stages = [...serviceBlueprintStages(this._host.serviceBlueprint)];
    stages[stageIndex] = nextStage;

    if (nextStage.stageKey !== previousStageKey) {
      stages = stages.map((stage) =>
        stage.stageKey === nextStage.stageKey
          ? stage
          : {
              ...stage,
              routes: (stage.routes ?? []).map((route) => ({
                ...route,
                target: route.target === previousStageKey ? nextStage.stageKey : route.target,
              })),
            }
      );
      gateways = serviceBlueprintGateways(this._host.serviceBlueprint).map((gateway) => ({
        ...gateway,
        routes: (gateway.routes ?? []).map((route) => ({
          ...route,
          target: route.target === previousStageKey ? nextStage.stageKey : route.target,
        })),
      }));
      if (initialStageKey === previousStageKey) {
        initialStageKey = nextStage.stageKey;
      }
    }

    const serviceBlueprint: ServiceBlueprint = {
      ...this._host.serviceBlueprint,
      initialStage: initialStageKey,
      stages,
      gateways,
    };

    this._host.emitUpdated(serviceBlueprint, { kind: 'stage', stageKey: nextStage.stageKey });
  }

  private readonly _updateSelectedStageActions = (event: CustomEvent<ActionsUpdatedDetail>) => {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    this._replaceSelectedStage({
      ...stage,
      actions: event.detail.actions,
    });
  };

  private readonly _updateStageTitle = (event: Event) => {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const nextTitle = (event.currentTarget as HTMLInputElement).value.trim();
    if (!nextTitle || nextTitle === stage.displayName) {
      return;
    }

    this._replaceSelectedStage({ ...stage, displayName: nextTitle });
    this._host.announce(`${nextTitle} title updated.`);
  };

  private _updateStageIcon(stage: StageDefinition, iconName: NodeIconName) {
    if (stage.icon === iconName) {
      return;
    }
    this._replaceSelectedStage({ ...stage, icon: iconName });
    this._host.announce(`${stage.displayName} icon updated.`);
  }

  private readonly _updateStageKey = (event: Event) => {
    const stage = this._host.selectedStage;
    if (!stage || !this._host.serviceBlueprint) {
      return;
    }

    const nextKey = (event.currentTarget as HTMLInputElement).value.trim();
    if (!nextKey) {
      this._stageKeyError = 'Stage key is required.';
      this._host.announce('Stage key is required.');
      return;
    }

    const duplicate = this._host.serviceBlueprint.stages.some(
      (candidate) => candidate.stageKey === nextKey && candidate.stageKey !== stage.stageKey
    );
    if (duplicate) {
      this._stageKeyError = 'Stage key must be unique.';
      this._host.announce(`Stage key ${nextKey} is already in use.`);
      return;
    }

    if (nextKey === stage.stageKey) {
      this._stageKeyError = null;
      return;
    }

    this._stageKeyError = null;
    this._replaceSelectedStage({ ...stage, stageKey: nextKey }, stage.stageKey);
    this._host.announce(`Stage key updated to ${nextKey}.`);
  };

  private readonly _updateStageDescription = (event: Event) => {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const nextDescription = (event.currentTarget as HTMLTextAreaElement).value.trim();
    const previousDescription = stage.description?.trim() ?? '';
    if (nextDescription === previousDescription) {
      return;
    }

    this._replaceSelectedStage({
      ...stage,
      description: nextDescription || undefined,
    });
    this._host.announce(`${stage.displayName} description updated.`);
  };

  private readonly _updateStageQueue = (event: Event) => {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const queueKey = (event.currentTarget as HTMLInputElement).value;
    const nextStage = applyQueueToStage(stage, queueKey);

    this._replaceSelectedStage(nextStage);
    this._host.announce(`${stage.displayName} queue updated.`);
  };

  private readonly _updateStageType = (event: Event) => {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    const nextStage: StageDefinition = {
      ...stage,
      stageType: (event.currentTarget as HTMLSelectElement).value as StageKind,
    };

    this._replaceSelectedStage(nextStage);
    this._host.announce(`${stage.displayName} type updated.`);
  };

  private _replaceSelectedStageComponents(nextComponents: Component[]) {
    const stage = this._host.selectedStage;
    if (!stage) {
      return;
    }

    this._replaceSelectedStage({ ...stage, components: nextComponents });
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

    this._replaceSelectedStageComponents(components);
    this._expandedComponentIndex = newIndex;
    this._newlyAddedComponentIndex = newIndex;
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
    this._replaceSelectedStageComponents(nextComponents);
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
    this._replaceSelectedStageComponents(nextComponents);
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

  renderStage(stage: StageDefinition) {
    const components = stage.components ?? [];
    const actions = stage.actions ?? [];
    const outgoing = this._selectedStageOutgoing(stage);
    const stageType = stage.stageType ?? 'Question';
    const queueKey = stageQueueKey(stage);
    const queueLabel = stageQueueLabel(this._host.serviceBlueprint, queueKey, this._host.availableQueues);
    const queueEyebrow = `${queueLabel} queue`;
    const queueOptionsId = `stage-queue-options-${stage.stageKey}`;
    const unreachable = this._host.serviceBlueprint
      ? serviceBlueprintUnreachableStages(this._host.serviceBlueprint).some((candidate) => candidate.stageKey === stage.stageKey)
      : false;
    const orphaned = this._host.serviceBlueprint
      ? serviceBlueprintOrphanedStages(this._host.serviceBlueprint).some((candidate) => candidate.stageKey === stage.stageKey)
      : false;
    const deadEnd = this._host.serviceBlueprint
      ? serviceBlueprintDeadEndStages(this._host.serviceBlueprint).some((candidate) => candidate.stageKey === stage.stageKey)
      : false;
    const validationMessages = [
      ...(this._stageKeyError ? [this._stageKeyError] : []),
      ...(orphaned ? ['This stage is disconnected from the service blueprint. Add at least one route to connect it.'] : []),
      ...(deadEnd || (outgoing.length === 0 && !isTerminalStage(stage))
        ? ['Add at least one outgoing route before publishing this stage.']
        : []),
      ...(unreachable ? ['This stage is unreachable from the service blueprint start. Add or retarget an incoming route.'] : []),
    ];

    return html`
      <article
        class="inspector-panel"
        data-wayfinder-stage-detail="${stage.stageKey}"
        data-wayfinder-inspector-kind="stage"
        aria-labelledby="inspector-stage-title"
      >
        <div class="inspector-header">
          <div>
            <p class="eyebrow">${queueEyebrow}</p>
            <h2 id="inspector-stage-title" class="stage-title">${stage.displayName}</h2>
          </div>
          <span class="stage-kind-badge">${stageKindLabel(stageType)}</span>
        </div>

        ${
          validationMessages.length > 0
            ? html`
              <section class="inspector-section validation-section" aria-labelledby="stage-validation-heading">
                <h3 id="stage-validation-heading" class="section-heading">Validation</h3>
                <ul class="validation-list">
                  ${validationMessages.map((message) => html`<li>${message}</li>`)}
                </ul>
              </section>
            `
            : nothing
        }

        <section class="inspector-section" aria-labelledby="stage-basics-heading">
          <h3 id="stage-basics-heading" class="section-heading">Stage details</h3>
          <div class="field-grid">
            <label class="field-block">
              <span class="field-label">Title</span>
              <input
                class="field-control"
                data-wayfinder-stage-title
                .value=${stage.displayName}
                @change=${this._updateStageTitle}
              />
            </label>
            <label class="field-block">
              <span class="field-label-row">
                <span class="field-label">Key</span>
                <wayfinder-inline-help
                  label="Stage key help"
                  message="Use a stable, machine-friendly key. Transitions, validation links, and saved service blueprint JSON all depend on this value staying predictable."
                ></wayfinder-inline-help>
              </span>
              <input
                class="field-control ${this._stageKeyError ? 'field-control-error' : ''}"
                data-wayfinder-stage-key
                aria-invalid=${String(Boolean(this._stageKeyError))}
                .value=${stage.stageKey}
                @input=${() => {
                  this._stageKeyError = null;
                }}
                @change=${this._updateStageKey}
              />
              ${
                this._stageKeyError ? html`<span class="field-error" data-wayfinder-stage-key-error>${this._stageKeyError}</span>` : nothing
              }
            </label>
            <label class="field-block">
              <span class="field-label-row">
                <span class="field-label">Queue</span>
                <wayfinder-inline-help
                  label="Queue help"
                  message="Use the queue name that owns this work, for example applicant, reviewer, finance, or planning. The editor keeps the internal actor and role-gate fields aligned from this queue value."
                ></wayfinder-inline-help>
              </span>
              <input
                class="field-control"
                data-wayfinder-stage-queue
                .value=${queueKey}
                list=${queueOptionsId}
                placeholder="planning-officer"
                @change=${this._updateStageQueue}
              />
              <datalist id=${queueOptionsId}>
                ${serviceBlueprintQueueOptions(this._host.serviceBlueprint, this._host.availableQueues).map(
                  (option) => html`
                  <option value=${option}>${stageQueueLabel(this._host.serviceBlueprint, option, this._host.availableQueues)}</option>
                `
                )}
              </datalist>
            </label>
            <label class="field-block">
              <span class="field-label">Type</span>
              <select class="field-control" data-wayfinder-stage-type @change=${this._updateStageType}>
                ${STAGE_KIND_OPTIONS.map(
                  (option) => html`
                  <option value=${option.value} ?selected=${stageType === option.value}>${option.label}</option>
                `
                )}
              </select>
            </label>
          </div>

          <div class="field-block field-block-full">
            <span class="field-label">Icon</span>
            ${renderIconPicker(stage.icon ?? defaultIconForStage(stage), (iconName) => this._updateStageIcon(stage, iconName))}
          </div>

          <label class="field-block field-block-full">
            <span class="field-label">Description</span>
            <textarea
              class="field-control field-textarea"
              data-wayfinder-stage-description
              .value=${stage.description ?? ''}
              @change=${this._updateStageDescription}
            ></textarea>
          </label>
        </section>

        <section class="inspector-section" aria-labelledby="stage-actions-heading">
          <div class="section-header-row">
            <h3 id="stage-actions-heading" class="section-heading">Actions</h3>
            <span class="section-meta">${actions.length} configured</span>
          </div>
          <wayfinder-stage-action-editor
            .actions=${actions}
            .actionCatalog=${this._host.actionCatalog}
            .supportSystemCatalog=${this._host.supportSystemCatalog}
            .supportSystemFieldReferences=${this._host.supportSystemFieldReferences}
            .selectedActionIndex=${this._host.selectedActionIndex}
            target="stage"
            subject-label="stage"
            @actions-updated=${this._updateSelectedStageActions}
            @action-selected=${this._handleActionSelected}
          ></wayfinder-stage-action-editor>
        </section>

        <section class="inspector-section" aria-labelledby="stage-transitions-heading">
          <div class="section-header-row">
            <h3 id="stage-transitions-heading" class="section-heading">Outgoing routes</h3>
            <button
              type="button"
              class="secondary-button"
              data-wayfinder-add-route
              aria-label="Add route from ${stage.displayName}"
              @click=${this._routes.addRouteFromSelection}
            >+ Add route</button>
          </div>
          ${
            outgoing.length === 0
              ? html`<p class="section-empty">No routes yet. Use <strong>+ Add route</strong> above to send this stage to its next destination.</p>`
              : html`
                <ul class="gateway-route-list" role="list">
                  ${outgoing.map(
                    (transition) => html`
                    <li
                      class="gateway-route-item"
                      data-wayfinder-route-target="${transition.toStage}"
                      data-wayfinder-route-id="${transition.routeId}"
                    >
                      ${this._routes.renderRouteEditor(transition, transition.routeIndex)}
                    </li>
                  `
                  )}
                </ul>
              `
          }
        </section>

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
      </article>
    `;
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
