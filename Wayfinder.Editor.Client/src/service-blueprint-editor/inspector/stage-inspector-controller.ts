import type { ReactiveController } from 'lit';
import { ComponentsController } from './components-controller.js';
import { html, nothing } from 'lit';
import type { Component, StageDefinition, StageKind, ServiceBlueprint } from '../types.js';
import { serviceBlueprintGateways, serviceBlueprintStages } from '../types.js';
import { defaultIconForStage, type NodeIconName } from '../graph/node-icons.js';
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
import type { RouteListController } from './route-list-controller.js';
import type { RouteEditorController } from './route-editor-controller.js';
import { renderIconPicker } from './node-icon-picker.js';

/** The properties panel for a selected stage: identity, queue, type, actions, components and its routes. */
export class StageInspectorController implements ReactiveController {
  private _stageKeyErrorValue: string | null = null;
  private _lastStageKey: string | null = null;
  private readonly _components: ComponentsController;

  constructor(
    private readonly _host: InspectorHost,
    private readonly _routes: RouteEditorController,
    private readonly _routeList: RouteListController
  ) {
    _host.addController(this);
    this._components = new ComponentsController(_host, (components) => this._replaceSelectedStageComponents(components));
  }

  private get _stageKeyError() {
    return this._stageKeyErrorValue;
  }

  private set _stageKeyError(value: string | null) {
    this._stageKeyErrorValue = value;
    this._host.requestUpdate();
  }

  /** The key error belongs to the stage they were raised on. */
  hostUpdate() {
    const stageKey = this._host.selectedStage?.stageKey ?? null;
    if (stageKey !== this._lastStageKey) {
      this._lastStageKey = stageKey;
      this._stageKeyErrorValue = null;
    }
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

  renderStage(stage: StageDefinition) {
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
              @click=${this._routeList.addRouteFromSelection}
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

        ${this._components.renderSection(stage)}
      </article>
    `;
  }
}
