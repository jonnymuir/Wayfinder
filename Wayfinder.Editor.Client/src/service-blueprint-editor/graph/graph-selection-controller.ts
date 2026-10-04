import type { ReactiveController, ReactiveControllerHost } from 'lit';
import type { ServiceBlueprint, ServiceBlueprintGatewayDefinition } from '../types.js';
import { flattenRoutes } from '../route-model.js';

type SelectionKind = 'stage' | 'transition' | 'gateway';

export type GraphSelectionDetail = {
  kind: SelectionKind;
  stageKey?: string;
  transitionIndex?: number;
  gatewayKey?: string;
};

export interface GraphSelectionContext {
  blueprint(): ServiceBlueprint | null;
  /** What the host's own `selected*` properties currently say. */
  properties(): { stageKey: string | null; transitionIndex: number | null; gatewayKey: string | null };
  labelFor(nodeKey: string): string;
  queueLabelForGateway(gateway: ServiceBlueprintGatewayDefinition): string;
  announce(message: string): void;
  dispatch(event: Event): void;
}

type OpenOptions = { openInspector?: boolean };

const bubbling = <T>(name: string, detail: T) => new CustomEvent<T>(name, { detail, bubbles: true, composed: true });

/**
 * Which stage, gateway or route is selected on the canvas. The host may drive it through its
 * `selected*` properties and the author drives it by clicking; either way a selection that no longer
 * exists in the blueprint clears. Selecting announces itself and tells the host (and, on request, the
 * inspector).
 */
export class GraphSelectionController implements ReactiveController {
  private _stageKey: string | null = null;
  private _transitionIndex: number | null = null;
  private _gatewayKey: string | null = null;

  constructor(
    private readonly _host: ReactiveControllerHost,
    private readonly _context: GraphSelectionContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  get stageKey() {
    return this._stageKey;
  }

  get transitionIndex() {
    return this._transitionIndex;
  }

  get gatewayKey() {
    return this._gatewayKey;
  }

  /** The current selection as an event payload, or null if nothing is selected. */
  get detail(): GraphSelectionDetail | null {
    if (this._stageKey) return { kind: 'stage', stageKey: this._stageKey };
    if (this._gatewayKey) return { kind: 'gateway', gatewayKey: this._gatewayKey };
    return this._transitionIndex === null ? null : { kind: 'transition', transitionIndex: this._transitionIndex };
  }

  /** Takes up the host's `selected*` properties when they change, and drops anything the blueprint no longer has. */
  sync(changed: Map<string, unknown>) {
    const properties = this._context.properties();
    if (changed.has('selectedStageKey')) this._stageKey = properties.stageKey;
    if (changed.has('selectedTransitionIndex')) this._transitionIndex = properties.transitionIndex;
    if (changed.has('selectedGatewayKey')) this._gatewayKey = properties.gatewayKey;

    const blueprint = this._context.blueprint();
    if (this._stageKey && !blueprint?.stages.some((stage) => stage.stageKey === this._stageKey)) this._stageKey = null;
    if (this._gatewayKey && !blueprint?.gateways?.some((gateway) => gateway.key === this._gatewayKey)) this._gatewayKey = null;
    if (this._transitionIndex !== null && !flattenRoutes(blueprint)[this._transitionIndex]) this._transitionIndex = null;
  }

  /** Quietly selects a route without announcing — for a route the author just created. */
  highlightTransition(index: number) {
    this._transitionIndex = index;
    this._stageKey = null;
    this._gatewayKey = null;
    this._host.requestUpdate();
  }

  /** Selects a stage without announcing or emitting — for one the author just created, which announces itself. */
  selectQuietly(selection: { kind: 'stage'; stageKey: string }) {
    this._stageKey = selection.stageKey;
    this._transitionIndex = null;
    this._gatewayKey = null;
    this._host.requestUpdate();
  }

  clearTransition() {
    this._transitionIndex = null;
    this._host.requestUpdate();
  }

  clearStage() {
    this._stageKey = null;
    this._host.requestUpdate();
  }

  clearGateway() {
    this._gatewayKey = null;
    this._host.requestUpdate();
  }

  announceChange(detail: GraphSelectionDetail) {
    this._context.dispatch(bubbling('selection-change', detail));
  }

  requestInspector(detail: GraphSelectionDetail) {
    this._context.dispatch(bubbling('inspector-requested', detail));
  }

  selectStage(stageKey: string, options?: OpenOptions) {
    this._stageKey = stageKey;
    this._transitionIndex = null;
    this._gatewayKey = null;
    this._host.requestUpdate();

    this._context.dispatch(bubbling('stage-selected', { stageKey }));
    this.announceChange({ kind: 'stage', stageKey });
    this._context.announce(`Stage “${this._context.labelFor(stageKey)}” selected.`);
    if (options?.openInspector) {
      this.requestInspector({ kind: 'stage', stageKey });
    }
  }

  selectGateway(gatewayKey: string, options?: OpenOptions) {
    const gateway = this._context.blueprint()?.gateways?.find((candidate) => candidate.key === gatewayKey);
    if (!gateway) {
      return;
    }

    this._gatewayKey = gatewayKey;
    this._stageKey = null;
    this._transitionIndex = null;
    this._host.requestUpdate();

    this._context.dispatch(bubbling('gateway-selected', { gatewayKey }));
    this.announceChange({ kind: 'gateway', gatewayKey });
    this._context.announce(
      `Gateway “${gateway.displayName}” selected. ${gateway.gatewayType} gateway in the ${this._context.queueLabelForGateway(gateway)} queue.`
    );
    if (options?.openInspector) {
      this.requestInspector({ kind: 'gateway', gatewayKey });
    }
  }

  selectTransition(index: number, options?: OpenOptions) {
    const transition = flattenRoutes(this._context.blueprint())[index];
    if (!transition) {
      return;
    }

    this.highlightTransition(index);
    this._context.dispatch(bubbling('transition-selected', { transitionIndex: index }));
    this.announceChange({ kind: 'transition', transitionIndex: index });
    this._context.announce(
      `Transition “${transition.action}” selected, from ${this._context.labelFor(transition.fromStage)} to ${this._context.labelFor(transition.toStage)}.`
    );
    if (options?.openInspector) {
      this.requestInspector({ kind: 'transition', transitionIndex: index });
    }
  }
}
