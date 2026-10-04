import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { ServiceBlueprintGatewayDefinition, RouteView } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import { buildPropertyReferenceContext } from '../component-property-references.js';
import { stageQueueKey } from '../stage-assignment.js';
import { deriveGatewayBindings, type GatewayBinding } from '../gateway-representation.js';
import { TRANSITION_ACTION_OPTIONS, transitionQuickAction } from '../gateway-route-conditions.js';
import { addRoute, deleteRoute, findOrCreateSplitGateway, flattenRoutes, newRouteId, updateRoute } from '../route-model.js';
import type { ExpressionCompletionItem } from '../wayfinder-calculation-expression-editor.js';
import { tryParseExpression } from '../calculation-runtime.js';
import type { ActionSelectedDetail, ActionsUpdatedDetail, InspectorHost } from './inspector-host.js';

/** Edits the routes leaving a stage or gateway (and arriving at a join): the route list, each route's editor, and add/delete. */
export class RouteEditorController implements ReactiveController {
  /** Tracks the route id of a just-created route so its target picker can take focus once rendered. */
  private _newlyAddedRouteId: string | null = null;

  constructor(private readonly _host: InspectorHost) {
    _host.addController(this);
  }

  hostUpdated() {
    if (!this._newlyAddedRouteId) {
      return;
    }
    const routeId = this._newlyAddedRouteId;
    this._newlyAddedRouteId = null;
    requestAnimationFrame(() => {
      const container = this._host.shadowRoot?.querySelector<HTMLElement>(`[data-wayfinder-route-id="${routeId}"]`);
      container?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      container?.querySelector<HTMLElement>('[data-wayfinder-route-target-select]')?.focus();
    });
  }

  /**
   * Insertable-name completions for a route's showWhen expression editor — every captured input
   * field blueprint-wide plus every calculated field, the identical two-source construction
   * wayfinder-calculations-editor.ts uses for a stage validation's when/rule completions. A route
   * can reference any field captured anywhere earlier in the journey, the same as a stage
   * validation rule can, so this is deliberately blueprint-wide, not scoped to the route's own
   * source stage.
   */
  get routeConditionCompletions(): ExpressionCompletionItem[] {
    const context = buildPropertyReferenceContext(this._host.serviceBlueprint, undefined, this._host.componentCatalog);
    return [
      ...context.allFields.map((field) => ({ name: field.fieldKey, detail: field.label })),
      ...context.calculationFieldNames.map((name) => ({ name, detail: 'field' })),
    ];
  }

  stageLabel(stageKey: string) {
    return (
      this._host.serviceBlueprint?.stages.find((stage) => stage.stageKey === stageKey)?.displayName ??
      serviceBlueprintGateways(this._host.serviceBlueprint).find((gateway) => gateway.key === stageKey)?.displayName ??
      stageKey
    );
  }

  gatewayLabel(gatewayKey: string) {
    return serviceBlueprintGateways(this._host.serviceBlueprint).find((gateway) => gateway.key === gatewayKey)?.displayName ?? gatewayKey;
  }

  private _routeDescriptor(transition: RouteView) {
    const fromStage = this.stageLabel(transition.fromStage);
    const fromGateway = transition.fromGateway ? this.gatewayLabel(transition.fromGateway) : null;
    const toGateway = transition.toGateway ? this.gatewayLabel(transition.toGateway) : null;
    const toStage = this.stageLabel(transition.toStage);

    const ariaParts = [`from ${fromStage}`];
    if (fromGateway) ariaParts.push(`via split gateway ${fromGateway}`);
    if (toGateway) ariaParts.push(`via join gateway ${toGateway}`);
    ariaParts.push(`to ${toStage}`);
    const ariaLabel = ariaParts.join(', ');

    const visibleTokens = [fromStage, fromGateway, toGateway, toStage].filter((token): token is string => Boolean(token));
    const arrow = html`<span aria-hidden="true"> → </span>`;
    const visible = visibleTokens.map((token, index) => (index === 0 ? html`<span>${token}</span>` : html`${arrow}<span>${token}</span>`));

    return html`<span aria-label=${ariaLabel}>${visible}</span>`;
  }

  private _availableJoinGatewaysForStage(stageKey: string) {
    if (!this._host.serviceBlueprint) {
      return [];
    }

    const stage = this._host.serviceBlueprint.stages.find((candidate) => candidate.stageKey === stageKey);
    const queueKey = stage ? stageQueueKey(stage) : '';

    return deriveGatewayBindings(this._host.serviceBlueprint)
      .filter((binding) => binding.gateway.gatewayType === 'Join')
      .filter((binding) => binding.anchorStageKey === stageKey || (!binding.anchorStageKey && binding.queueKey === queueKey))
      .map((binding) => binding.gateway);
  }

  private _replaceSelectedTransition(nextTransition: RouteView, transitionIndex: number) {
    if (!this._host.serviceBlueprint) {
      return;
    }

    const transitions = flattenRoutes(this._host.serviceBlueprint);
    const previous = transitions[transitionIndex];
    if (!previous) {
      return;
    }

    // Slice C: edits address a route by (ownerKey, routeId) regardless of whether the owner is a
    // gateway or a stage — updateRoute itself already handles both (mutateRouteOwners matches
    // routeId across both stages[].routes and gateways[].routes). Project the mutation through it
    // so it survives serialisation either way.
    const ownerKey = previous.key || nextTransition.key;
    const routeId = previous.routeId || nextTransition.routeId;
    if (!ownerKey || !routeId) {
      return;
    }
    const nextServiceBlueprint = updateRoute(this._host.serviceBlueprint, { routeId }, (route) => ({
      ...route,
      target: nextTransition.toStage || route.target,
      trigger: nextTransition.action || route.trigger,
      showWhen: nextTransition.showWhen,
      requiresRole: nextTransition.requiresRole,
      actions: nextTransition.actions ?? route.actions,
    }));

    // Preserve whichever kind of panel this edit actually came from — a gateway's own "Outgoing
    // routes"/"Incoming routes", or (since a stage's own routes became fully editable in place,
    // not just gateway-owned ones) a stage's own "Outgoing routes". Dropping this to `null` when
    // edited from a stage panel used to bounce the properties panel back to its empty state after
    // every keystroke, discarding the very selection the author was actively editing.
    const selectedGatewayKey = this._host.selectedGateway?.key;
    const selectedStageKey = this._host.selectedStage?.stageKey;
    this._host.emitUpdated(
      nextServiceBlueprint,
      selectedGatewayKey
        ? { kind: 'gateway', gatewayKey: selectedGatewayKey }
        : selectedStageKey
          ? { kind: 'stage', stageKey: selectedStageKey }
          : null
    );
  }

  private readonly _updateRouteActions = (event: CustomEvent<ActionsUpdatedDetail>) => {
    if (!this._host.serviceBlueprint) {
      return;
    }
    const target = event.currentTarget as HTMLElement | null;
    const idxAttr = target?.dataset.wayfinderRouteIndex;
    const transitionIndex = idxAttr ? Number(idxAttr) : NaN;
    if (!Number.isInteger(transitionIndex)) {
      return;
    }
    const transition = flattenRoutes(this._host.serviceBlueprint)[transitionIndex];
    if (!transition) {
      return;
    }
    this._replaceSelectedTransition({ ...transition, actions: event.detail.actions }, transitionIndex);
  };

  private readonly _handleRouteActionSelected = (event: CustomEvent<ActionSelectedDetail>) => {
    event.stopPropagation();
    const target = event.currentTarget as HTMLElement | null;
    const idxAttr = target?.dataset.wayfinderRouteIndex;
    const transitionIndex = idxAttr ? Number(idxAttr) : NaN;
    const detail: ActionSelectedDetail = {
      ...event.detail,
      target: 'transition',
      transitionIndex: Number.isInteger(transitionIndex) ? transitionIndex : undefined,
    };
    this._host.dispatchEvent(
      new CustomEvent<ActionSelectedDetail>('action-selected', {
        detail,
        bubbles: true,
        composed: true,
      })
    );
  };

  private _routeIndexFromEvent(event: Event): number | null {
    const target = event.currentTarget as HTMLElement | null;
    const raw = target?.dataset.wayfinderRouteIndex;
    const index = raw ? Number(raw) : NaN;
    return Number.isInteger(index) ? index : null;
  }

  private _routeTransitionFromEvent(event: Event): { index: number; transition: RouteView } | null {
    if (!this._host.serviceBlueprint) {
      return null;
    }
    const index = this._routeIndexFromEvent(event);
    if (index === null) {
      return null;
    }
    const transition = flattenRoutes(this._host.serviceBlueprint)[index];
    return transition ? { index, transition } : null;
  }

  private readonly _updateRouteLabel = (event: Event) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const action = (event.currentTarget as HTMLInputElement).value.trim();
    if (!action || action === ctx.transition.action) return;
    this._replaceSelectedTransition({ ...ctx.transition, action }, ctx.index);
    this._host.announce(`Route label updated to ${action}.`);
  };

  private readonly _updateRouteActionPreset = (event: Event) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const nextAction = (event.currentTarget as HTMLSelectElement).value;
    if (nextAction === 'custom' || nextAction === ctx.transition.action) return;
    this._replaceSelectedTransition({ ...ctx.transition, action: nextAction }, ctx.index);
    this._host.announce(`Route preset updated to ${nextAction}.`);
  };

  private readonly _updateRouteTarget = (event: Event) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const toStage = (event.currentTarget as HTMLSelectElement).value;
    if (!toStage || toStage === ctx.transition.toStage) return;
    this._replaceSelectedTransition({ ...ctx.transition, toStage }, ctx.index);
    this._host.announce(`Route now arrives at ${this.stageLabel(toStage)}.`);
  };

  private readonly _updateRouteToGateway = (event: Event) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const toGateway = (event.currentTarget as HTMLSelectElement).value || undefined;
    if (toGateway === ctx.transition.toGateway) return;
    this._replaceSelectedTransition({ ...ctx.transition, toGateway }, ctx.index);
    this._host.announce(
      toGateway ? `Route now arrives through ${this.gatewayLabel(toGateway)}.` : 'Route now arrives directly at the target stage.'
    );
  };

  private readonly _updateRouteShowWhen = (event: CustomEvent<{ value: string }>) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const showWhen = event.detail.value.trim() || undefined;
    if (showWhen === ctx.transition.showWhen) return;
    this._replaceSelectedTransition({ ...ctx.transition, showWhen }, ctx.index);
    this._host.announce(showWhen ? 'Route visibility condition updated.' : 'Route visibility condition cleared — always available.');
  };

  private readonly _updateRouteRole = (event: Event) => {
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const requiresRole = (event.currentTarget as HTMLInputElement).value.trim() || undefined;
    if (requiresRole === ctx.transition.requiresRole) return;
    this._replaceSelectedTransition({ ...ctx.transition, requiresRole }, ctx.index);
    this._host.announce(requiresRole ? `Role guard updated to ${requiresRole}.` : 'Role guard cleared.');
  };

  private readonly _deleteRoute = (event: Event) => {
    if (!this._host.serviceBlueprint) return;
    const ctx = this._routeTransitionFromEvent(event);
    if (!ctx) return;
    const ownerKey = ctx.transition.key;
    const routeId = ctx.transition.routeId;
    if (!ownerKey || !routeId) return;
    const nextServiceBlueprint = deleteRoute(this._host.serviceBlueprint, { gatewayKey: ownerKey, routeId });
    // Same fix as _replaceSelectedTransition: stay on whichever panel — gateway or stage — this
    // delete was actually triggered from, rather than always assuming a gateway.
    const selectedGatewayKey = this._host.selectedGateway?.key;
    const selectedStageKey = this._host.selectedStage?.stageKey;
    this._host.emitUpdated(
      nextServiceBlueprint,
      selectedGatewayKey
        ? { kind: 'gateway', gatewayKey: selectedGatewayKey }
        : selectedStageKey
          ? { kind: 'stage', stageKey: selectedStageKey }
          : null
    );
    this._host.announce(`Route ${ctx.transition.action} deleted.`);
  };

  readonly addRouteFromSelection = () => {
    if (!this._host.serviceBlueprint) return;

    const sourceStageKey =
      this._host.selectedStage?.stageKey ??
      deriveGatewayBindings(this._host.serviceBlueprint).find((binding) => binding.gateway.key === this._host.selectedGatewayKey)
        ?.anchorStageKey ??
      null;

    if (!sourceStageKey) return;

    const { serviceBlueprint: withGateway, gatewayKey } = findOrCreateSplitGateway(this._host.serviceBlueprint, sourceStageKey);

    const routeId = `${newRouteId(sourceStageKey, '', '')}-${Date.now().toString(36)}`;
    const nextRoute = {
      id: routeId,
      target: '',
      trigger: '',
      actions: [],
    };

    const nextServiceBlueprint = addRoute(withGateway, gatewayKey, nextRoute);
    this._newlyAddedRouteId = routeId;
    this._host.emitUpdated(nextServiceBlueprint, { kind: 'gateway', gatewayKey });
    this._host.announce('Route added — choose a destination.');
  };

  renderGatewayRoutes(gateway: ServiceBlueprintGatewayDefinition, binding: GatewayBinding | null) {
    if (!this._host.serviceBlueprint) return nothing;
    const isJoin = gateway.gatewayType === 'Join';
    const indices = binding?.relatedTransitionIndices ?? [];
    const routeNoun = isJoin ? 'Incoming routes' : 'Outgoing routes';
    const sourceStageLabel = gateway.displayName;

    return html`
      <section class="inspector-section" aria-labelledby="section-gateway-routes">
        <div class="section-header-row">
          <h3 id="section-gateway-routes" class="section-heading">${routeNoun}</h3>
          ${
            !isJoin
              ? html`
            <button
              type="button"
              class="secondary-button"
              data-wayfinder-add-route
              aria-label="Add route from ${sourceStageLabel}"
              @click=${this.addRouteFromSelection}
            >+ Add route</button>
          `
              : nothing
          }
        </div>
        ${
          indices.length === 0
            ? html`
              <p class="empty-section" data-wayfinder-gateway-routes-empty>
                No routes yet. Use <strong>+ Add route</strong> above to send this stage to its next destination.
              </p>
            `
            : html`
              <p class="action-summary" data-wayfinder-gateway-routes-summary>
                ${indices.length} ${indices.length === 1 ? 'route' : 'routes'} ${isJoin ? 'feed into' : 'leave'} this gateway.
              </p>
              <ul class="gateway-route-list" role="list">
                ${indices.map((transitionIndex) => {
                  const transition = flattenRoutes(this._host.serviceBlueprint)[transitionIndex];
                  if (!transition) return nothing;
                  return html`
                    <li
                      class="gateway-route-item"
                      data-wayfinder-gateway-route="${transitionIndex}"
                      data-wayfinder-route-target="${transition.toStage}"
                      data-wayfinder-route-id="${transition.routeId}"
                    >
                      ${this.renderRouteEditor(transition, transitionIndex)}
                    </li>
                  `;
                })}
              </ul>
            `
        }
      </section>
    `;
  }

  renderRouteEditor(transition: RouteView, transitionIndex: number) {
    const targetOptions = (this._host.serviceBlueprint?.stages ?? []).filter((stage) => stage.stageKey !== transition.fromStage);
    const joinGateways = this._availableJoinGatewaysForStage(transition.toStage);
    const idx = String(transitionIndex);
    const ariaId = `route-${transitionIndex}-title`;
    const targetEmpty = !transition.toStage;
    const targetWarningId = `route-${transitionIndex}-target-warning`;
    // A route this stage owns can target a gateway directly (the ordinary shape for any route
    // authored via "+ Add route", which always creates or reuses a Split pass-through) — the
    // "Target stage" <select> below only ever lists real stages, so a gateway target matches none
    // of its <option>s. Left alone, an unselected native <select> silently falls back to its
    // first non-disabled option — which is some unrelated stage, not the gateway this route
    // actually goes to. That's not just a display glitch: an author who "reselects" what looks
    // like the current value would genuinely retarget the route to that wrong stage. Detect it
    // and render an honest, non-editable readout instead — retargeting away from a gateway this
    // route was wired to (by "+ Add route" or by direct JSON authoring) isn't something this
    // control is meant to do; that's what deleting and re-adding the route is for.
    const targetIsGateway = Boolean(transition.toGateway);
    const targetIsUnresolved = !targetEmpty && !targetIsGateway && !targetOptions.some((stage) => stage.stageKey === transition.toStage);

    return html`
      <article
        class="gateway-route-editor"
        aria-labelledby="${ariaId}"
        data-wayfinder-route-detail="${transition.fromStage}-${transition.action}-${transition.toStage}"
      >
        <header class="gateway-route-editor-header">
          <h4 id="${ariaId}" class="gateway-route-title">${transition.action}</h4>
          <p class="action-summary gateway-routing-hint" data-wayfinder-route-descriptor>
            ${this._routeDescriptor(transition)}
          </p>
        </header>

        <div class="field-grid">
          <label class="field-block">
            <span class="field-label">Route label</span>
            <input
              class="field-control"
              data-wayfinder-route-label
              data-wayfinder-route-index="${idx}"
              .value=${transition.action}
              @change=${this._updateRouteLabel}
            />
          </label>
          <label class="field-block">
            <span class="field-label">Route preset</span>
            <select
              class="field-control"
              data-wayfinder-route-action
              data-wayfinder-route-index="${idx}"
              @change=${this._updateRouteActionPreset}
            >
              ${TRANSITION_ACTION_OPTIONS.map(
                (option) => html`
                <option value=${option.value} ?selected=${transitionQuickAction(transition.action) === option.value}>${option.label}</option>
              `
              )}
              <option value="custom" ?selected=${transitionQuickAction(transition.action) === 'custom'}>Custom label</option>
            </select>
          </label>
          <label class="field-block">
            <span class="field-label-row">
              <span class="field-label">Target stage</span>
              ${
                targetIsGateway || targetIsUnresolved
                  ? html`
                    <wayfinder-inline-help
                      label="Target stage help"
                      message="This route goes to a gateway rather than a stage directly — that's normal for a route created with + Add route. A gateway's own onward routing is edited by selecting it, not from here."
                    ></wayfinder-inline-help>
                  `
                  : nothing
              }
            </span>
            ${
              targetIsGateway || targetIsUnresolved
                ? html`
                  <p class="field-control field-static-value" data-wayfinder-route-target-gateway>
                    ${
                      targetIsGateway
                        ? html`<strong>${this.gatewayLabel(transition.toGateway!)}</strong> (gateway)`
                        : html`“${transition.toStage}” — not a stage in this service blueprint`
                    }
                  </p>
                `
                : html`
                  <select
                    class="field-control ${targetEmpty ? 'field-control-error' : ''}"
                    data-wayfinder-route-target-select
                    data-wayfinder-route-index="${idx}"
                    aria-invalid=${String(targetEmpty)}
                    aria-describedby=${targetEmpty ? targetWarningId : ''}
                    @change=${this._updateRouteTarget}
                  >
                    <option value="" ?selected=${targetEmpty} disabled>Choose a destination…</option>
                    ${targetOptions.map(
                      (stage) => html`
                      <option value=${stage.stageKey} ?selected=${stage.stageKey === transition.toStage}>${stage.displayName}</option>
                    `
                    )}
                  </select>
                  ${
                    targetEmpty
                      ? html`<span id="${targetWarningId}" class="field-error" data-wayfinder-route-target-warning>Choose a destination</span>`
                      : nothing
                  }
                `
            }
          </label>
          <label class="field-block">
            <span class="field-label">Arrive through</span>
            <select
              class="field-control"
              data-wayfinder-route-to-gateway
              data-wayfinder-route-index="${idx}"
              @change=${this._updateRouteToGateway}
            >
              <option value="">No join gateway</option>
              ${joinGateways.map(
                (g) => html`
                <option value=${g.key} ?selected=${g.key === transition.toGateway}>${g.displayName}</option>
              `
              )}
            </select>
          </label>
          <label class="field-block">
            <span class="field-label-row">
              <span class="field-label">Role guard</span>
              <wayfinder-inline-help
                label="Role guard help"
                message="Add a role only when this route should be limited to a specific actor such as reviewer or caseworker. Leave it blank when everyone on the route can use it."
              ></wayfinder-inline-help>
            </span>
            <input
              class="field-control"
              data-wayfinder-route-role
              data-wayfinder-route-index="${idx}"
              .value=${transition.requiresRole ?? ''}
              placeholder="reviewer"
              @change=${this._updateRouteRole}
            />
          </label>
        </div>

        ${
          transition.fromGateway
            ? nothing
            : (
                () => {
                  const showWhenParse = transition.showWhen?.trim() ? tryParseExpression(transition.showWhen) : null;
                  return html`
                <div class="field-grid">
                  <div class="field-block calc-expression-block">
                    <span class="field-label-row">
                      <span class="field-label">Available when (optional — leave blank for always available)</span>
                      <wayfinder-inline-help
                        label="Route visibility help"
                        message="A boolean expression in the same calculation language as a stage validation's when/rule. Leave blank for a route that's always offered. Set it so only one of several routes appears depending on the data captured so far — for example riskAssessment <> '' — rather than offering an action that would just get blocked with an error."
                      ></wayfinder-inline-help>
                    </span>
                    <wayfinder-calculation-expression-editor
                      data-wayfinder-route-index="${idx}"
                      .value=${transition.showWhen ?? ''}
                      .completions=${this.routeConditionCompletions}
                      label-text="${transition.action || 'route'} available when"
                      @expression-input=${this._updateRouteShowWhen}
                    ></wayfinder-calculation-expression-editor>
                    ${
                      showWhenParse && !showWhenParse.ok
                        ? html`<span class="calc-preview calc-preview-error" data-wayfinder-route-show-when-preview>${showWhenParse.message}</span>`
                        : nothing
                    }
                  </div>
                </div>
              `;
                }
              )()
        }

        <div class="action-buttons">
          <button
            type="button"
            class="icon-button danger-button"
            data-wayfinder-route-delete
            data-wayfinder-route-index="${idx}"
            @click=${this._deleteRoute}
          >
            Delete route
          </button>
        </div>

        <section class="inspector-subsection" aria-labelledby="${ariaId} section-route-actions-${idx}">
          <div class="section-header-row">
            <h5 id="section-route-actions-${idx}" class="section-heading">Route actions</h5>
            <span class="section-meta">${transition.actions?.length ?? 0} configured</span>
          </div>
          <wayfinder-stage-action-editor
            data-wayfinder-route-index="${idx}"
            .actions=${transition.actions ?? []}
            .actionCatalog=${this._host.actionCatalog}
            .supportSystemCatalog=${this._host.supportSystemCatalog}
            .supportSystemFieldReferences=${this._host.supportSystemFieldReferences}
            .selectedActionIndex=${this._host.selectedActionTransitionIndex === transitionIndex ? this._host.selectedActionIndex : null}
            target="transition"
            subject-label="transition"
            @actions-updated=${this._updateRouteActions}
            @action-selected=${this._handleRouteActionSelected}
          ></wayfinder-stage-action-editor>
        </section>
      </article>
    `;
  }
}
