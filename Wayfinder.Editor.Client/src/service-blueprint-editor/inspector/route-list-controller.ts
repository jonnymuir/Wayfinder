import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { ServiceBlueprintGatewayDefinition } from '../types.js';
import { deriveGatewayBindings, type GatewayBinding } from '../gateway-representation.js';
import { addRoute, findOrCreateSplitGateway, flattenRoutes, newRouteId } from '../route-model.js';
import type { InspectorHost } from './inspector-host.js';

import type { RouteEditorController } from './route-editor-controller.js';

/** A gateway's list of routes, and adding a new one from the selected stage or gateway. */
export class RouteListController implements ReactiveController {
  /** Tracks the route id of a just-created route so its target picker can take focus once rendered. */
  private _newlyAddedRouteId: string | null = null;

  constructor(
    private readonly _host: InspectorHost,
    private readonly _editor: RouteEditorController
  ) {
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
                      ${this._editor.renderRouteEditor(transition, transitionIndex)}
                    </li>
                  `;
                })}
              </ul>
            `
        }
      </section>
    `;
  }
}
