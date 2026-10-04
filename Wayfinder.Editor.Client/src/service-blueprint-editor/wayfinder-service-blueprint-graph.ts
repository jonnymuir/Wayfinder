import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ServiceBlueprint } from './types.js';
import { serviceBlueprintGateways } from './types.js';
import {
  stageQueueLabel,
  stageSurface,
  serviceBlueprintQueueOptions,
  type QueueDefinition,
  type StageSurface,
} from './stage-assignment.js';
import { gatewayQueueKey } from './gateway-representation.js';
import { deleteRoute, flattenRoutes } from './route-model.js';
import type { GraphCallbacks, GraphNodeMove, GraphProps } from './graph/graph-callbacks.js';
import { gatewayNodeId, parseGraphNodeId, stageNodeId } from './graph/service-blueprint-graph-layout.js';
import { applyAutoArrange, setRouteWaypoint } from './graph/service-blueprint-graph-layout-block.js';
import { applyNodeMoves, connectNodes, describeNodeMoves } from './graph/graph-canvas-edits.js';
import { CanvasBridgeController } from './graph/canvas-bridge-controller.js';
import { ContextMenuController, type ContextMenuCommands, type NodeTarget } from './graph/context-menu-controller.js';
import { GraphDialogsController } from './graph/graph-dialogs-controller.js';
import { GraphSelectionController, type GraphSelectionDetail } from './graph/graph-selection-controller.js';
import { renderGraphHud, renderWorkspaceEmptyState } from './graph/graph-hud.js';
import graphStyles from './wayfinder-service-blueprint-graph.css?inline';

type ServiceBlueprintUpdatedDetail = {
  serviceBlueprint: ServiceBlueprint;
  selection?: GraphSelectionDetail | null;
};

/**
 * ServiceBlueprint graph workspace for stage/transition authoring.
 *
 * Emits:
 *  - stage-selected CustomEvent<{ stageKey: string }>
 *  - transition-selected CustomEvent<{ transitionIndex: number }>
 *  - selection-change CustomEvent<GraphSelectionDetail>
 *  - inspector-requested CustomEvent<GraphSelectionDetail>
 *  - service-blueprint-updated CustomEvent<ServiceBlueprintUpdatedDetail>
 */
@customElement('wayfinder-service-blueprint-graph')
export class WayfinderServiceBlueprintGraphElement extends LitElement {
  @property({ attribute: false })
  serviceBlueprint: ServiceBlueprint | null = null;

  @property({ attribute: false })
  availableQueues: QueueDefinition[] = [];

  /**
   * Render the graph as a pure viewer — no toolbar create buttons, no creation
   * dialogs, no context menus. Selection and zoom remain available so the viewer
   * is keyboard-navigable. Defaults to false (full authoring surface).
   */
  @property({ type: Boolean, attribute: 'read-only', reflect: true })
  readOnly = false;

  /**
   * Hides this element's own title bar and workspace toolbar (Add stage/Add gateway/Tidy
   * layout/zoom/Fit) — used when a host (wayfinder-service-blueprint-editor) renders its own
   * consolidated toolbar instead and calls this element's public addStage/addGateway/
   * tidyLayout/zoomIn/zoomOut/fitToScreen/fitToWidth methods directly. Defaults to false so
   * every other context (Storybook stories, any other standalone embedding) keeps its own
   * fully self-contained toolbar unchanged.
   */
  @property({ type: Boolean, attribute: 'hide-own-toolbar' })
  hideOwnToolbar = false;

  /**
   * Declarative JSON form of {@link serviceBlueprint}. Lets the element be initialised
   * from HTML/Razor markup without JS wiring — Razor authors can write
   * `<wayfinder-service-blueprint-graph read-only service-blueprint-json='...'>` and skip the prop
   * assignment. When set, this attribute is parsed and assigned to `serviceBlueprint`.
   */
  @property({ type: String, attribute: 'service-blueprint-json' })
  serviceBlueprintJson: string | null = null;

  @property({ attribute: false })
  selectedStageKey: string | null = null;

  @property({ attribute: false })
  selectedTransitionIndex: number | null = null;

  @property({ attribute: false })
  selectedGatewayKey: string | null = null;

  // Simulation highlighting plumbing — no caller currently drives these (the Simulation tab that
  // used to was removed; see wayfinder-service-blueprint-editor.ts). Left in place rather than
  // torn out through graph-app.tsx/graph-bridge.ts/graph-callbacks.ts/graph-model.ts too: with no
  // caller, these three always default, so the .simulation-path/.simulation-current CSS below
  // never actually applies — inert, not broken. A future path-highlighting feature (recorded
  // history playback, a real simulation rebuild) could reuse this rather than reinventing it.
  @property({ attribute: false })
  simulationCurrentStageKey: string | null = null;

  @property({ attribute: false })
  simulationPathStageKeys: string[] = [];

  @property({ attribute: false })
  simulationPathTransitionIndices: number[] = [];

  @state()
  private _zoom = 1;

  private _lastMultiSelection: string[] = [];

  private readonly _selection = new GraphSelectionController(this, {
    blueprint: () => this.serviceBlueprint,
    properties: () => ({
      stageKey: this.selectedStageKey,
      transitionIndex: this.selectedTransitionIndex,
      gatewayKey: this.selectedGatewayKey,
    }),
    labelFor: (nodeKey) => this._labelFor(nodeKey),
    queueLabelForGateway: (gateway) => this._queueLabel(gatewayQueueKey(gateway) || 'public'),
    announce: (message) => this._announce(message),
    dispatch: (event) => void this.dispatchEvent(event),
  });

  private readonly _menu = new ContextMenuController(this, {
    bounds: () => this.getBoundingClientRect(),
    root: () => this.shadowRoot,
    commands: () => this._menuCommands,
  });

  private readonly _dialogs = new GraphDialogsController(this, {
    blueprint: () => this.serviceBlueprint,
    queueKeys: () => serviceBlueprintQueueOptions(this.serviceBlueprint, this.availableQueues),
    queueLabel: (queueKey) => this._queueLabel(queueKey),
    nodeLabel: (nodeKey) => this._labelFor(nodeKey),
    fallbackReturnTarget: () => this._menu.returnTarget,
    dismissContextMenu: () => this._menu.dismiss(false),
    announce: (message) => this._announce(message),
    stageCreated: (blueprint, stageKey) => {
      this._selection.selectQuietly({ kind: 'stage', stageKey });
      this._selection.announceChange({ kind: 'stage', stageKey });
      this._emitUpdated(blueprint, { kind: 'stage', stageKey });
      this._selection.requestInspector({ kind: 'stage', stageKey });
      // A new stage has no routes to anchor it near existing content — bring it into view.
      requestAnimationFrame(() => this._canvas.centerOnNode(stageNodeId(stageKey)));
    },
    gatewayCreated: (blueprint, gatewayKey) => {
      this._emitUpdated(blueprint, { kind: 'gateway', gatewayKey });
      requestAnimationFrame(() => this._canvas.centerOnNode(gatewayNodeId(gatewayKey)));
    },
    nodeDeleted: (blueprint, node) => {
      if (node === 'stage') {
        this._selection.clearStage();
      } else {
        this._selection.clearGateway();
      }
      this._selection.clearTransition();
      this._emitUpdated(blueprint, null);
    },
  });

  private readonly _canvas = new CanvasBridgeController(this, {
    root: () => this.shadowRoot,
    readyHost: () => this,
    isConnected: () => this.isConnected,
    props: () => this._graphProps(),
    callbacks: () => this._graphCallbacks(),
  });

  protected willUpdate(changed: Map<string, unknown>) {
    if (changed.has('serviceBlueprintJson') && this.serviceBlueprintJson) {
      try {
        this.serviceBlueprint = JSON.parse(this.serviceBlueprintJson) as ServiceBlueprint;
      } catch (error) {
        console.error('wayfinder-service-blueprint-graph: service-blueprint-json could not be parsed.', error);
      }
    }
    this._selection.sync(changed);
  }

  private _graphProps(): GraphProps {
    return {
      serviceBlueprint: this.serviceBlueprint,
      availableQueues: this.availableQueues,
      readOnly: this.readOnly,
      selectedStageKey: this._selection.stageKey,
      selectedGatewayKey: this._selection.gatewayKey,
      selectedTransitionIndex: this._selection.transitionIndex,
      simulationCurrentStageKey: this.simulationCurrentStageKey,
      simulationPathStageKeys: this.simulationPathStageKeys,
      simulationPathTransitionIndices: this.simulationPathTransitionIndices,
    };
  }

  /** The gestures the canvas reports, each ignored while read-only. */
  private _graphCallbacks(): GraphCallbacks {
    const editable =
      <A extends unknown[]>(run: (...args: A) => void) =>
      (...args: A) => {
        if (!this.readOnly) run(...args);
      };
    return {
      selectStage: (stageKey, options) => this._selection.selectStage(stageKey, options),
      selectGateway: (gatewayKey, options) => this._selection.selectGateway(gatewayKey, options),
      selectTransition: (index, options) => this._selection.selectTransition(index, options),
      requestDeleteStage: editable((stageKey, returnTarget) => this._dialogs.openDelete('stage', stageKey, returnTarget)),
      requestDeleteGateway: editable((gatewayKey, returnTarget) => this._dialogs.openDelete('gateway', gatewayKey, returnTarget)),
      requestDeleteTransition: editable((index) => this._deleteTransition(index)),
      openContextMenu: editable((position, target, returnTarget) => this._menu.open(position, target, returnTarget)),
      paneClicked: () => this._menu.dismiss(false),
      nodesMoved: editable((moves) => this._handleNodesMoved(moves)),
      routeWaypointMoved: editable((edgeKey, position) => this._handleRouteWaypointMoved(edgeKey, position)),
      connectRequested: editable((connection) => this._handleConnectRequested(connection)),
      multiSelectionChanged: (nodeIds) => this._handleMultiSelection(nodeIds),
      laneFocused: (lane) =>
        this._announce(`${lane.label} queue. ${lane.stageCount} stage${lane.stageCount === 1 ? '' : 's'}. ${lane.description}.`),
      zoomChanged: (zoom) => {
        this._zoom = Number(zoom.toFixed(2));
        // Relayed so a host rendering its own consolidated toolbar (hideOwnToolbar) can show the
        // current zoom percentage without needing this element's own hidden HUD.
        this.dispatchEvent(
          new CustomEvent<{ zoom: number }>('zoom-changed', { detail: { zoom: this._zoom }, bubbles: true, composed: true })
        );
      },
      ready: () => this.setAttribute('data-wayfinder-graph-ready', 'true'),
    };
  }

  /** React Flow reports selection with a fresh array identity on every render — only forward genuine changes or the host re-render loops. */
  private _handleMultiSelection(nodeIds: string[]) {
    const unchanged =
      nodeIds.length === this._lastMultiSelection.length && nodeIds.every((id, index) => id === this._lastMultiSelection[index]);
    if (unchanged) {
      return;
    }
    this._lastMultiSelection = nodeIds;
    this.dispatchEvent(
      new CustomEvent<{ nodeIds: string[] }>('graph-multi-selection', { detail: { nodeIds }, bubbles: true, composed: true })
    );
  }

  // ── gestures on the canvas ────────────────────────────────────────────────

  private _handleNodesMoved(moves: GraphNodeMove[]) {
    if (!this.serviceBlueprint || moves.length === 0) {
      return;
    }
    this._emitUpdated(applyNodeMoves(this.serviceBlueprint, moves), this._selection.detail);
    this._announce(
      describeNodeMoves(
        moves,
        (key) => this._labelFor(key),
        (key) => this._queueLabel(key)
      )
    );
  }

  private _handleRouteWaypointMoved(edgeKey: string, position: { x: number; y: number } | null) {
    if (!this.serviceBlueprint) {
      return;
    }
    this._emitUpdated(setRouteWaypoint(this.serviceBlueprint, edgeKey, position), this._selection.detail);
    this._announce(position ? 'Route bend point moved.' : 'Route reset to its automatic path.');
  }

  private _handleConnectRequested(connection: { sourceId: string; targetId: string }) {
    if (!this.serviceBlueprint) {
      return;
    }

    const result = connectNodes(this.serviceBlueprint, connection.sourceId, connection.targetId);
    if (result === null) {
      return;
    }

    const [from, to] = [connection.sourceId, connection.targetId].map((id) => this._labelFor(parseGraphNodeId(id).key));
    if (result === 'duplicate') {
      this._announce(`A “continue” route from ${from} to ${to} already exists.`);
      return;
    }

    const index = flattenRoutes(result.blueprint).findIndex((view) => view.routeId === result.routeId);
    const selection: GraphSelectionDetail | null = index >= 0 ? { kind: 'transition', transitionIndex: index } : null;
    if (selection) {
      this._selection.highlightTransition(index);
    }
    this._emitUpdated(result.blueprint, selection);
    if (selection) {
      this._selection.announceChange(selection);
      this._selection.requestInspector(selection);
    }
    this._announce(`Route added from ${from} to ${to}.`);
  }

  private _deleteTransition(index: number) {
    const transition = flattenRoutes(this.serviceBlueprint)[index];
    if (!this.serviceBlueprint || !transition?.key || !transition.routeId) {
      return;
    }

    this._selection.clearTransition();
    this._emitUpdated(deleteRoute(this.serviceBlueprint, { gatewayKey: transition.key, routeId: transition.routeId }), null);
    this._menu.dismiss(false);
    this._announce(`Transition “${transition.action}” deleted.`);
  }

  // ── context menu commands ─────────────────────────────────────────────────

  private readonly _menuCommands: ContextMenuCommands = {
    addStage: (afterStageKey) => this._openAddStage(afterStageKey ?? this._selection.stageKey, afterStageKey ? 'after' : 'append'),
    fitToScreen: () => this.fitToScreen(),
    openInspector: (target) => this._openInspector(target),
    copyJson: (target) => this._copyJson(target),
    remove: (target) => this._remove(target),
  };

  private _openInspector(target: NodeTarget) {
    const options = { openInspector: true };
    if (target.kind === 'stage') this._selection.selectStage(target.stageKey, options);
    else if (target.kind === 'gateway') this._selection.selectGateway(target.gatewayKey, options);
    else this._selection.selectTransition(target.transitionIndex, options);
  }

  private _remove(target: NodeTarget) {
    if (target.kind === 'stage') this._dialogs.openDelete('stage', target.stageKey);
    else if (target.kind === 'gateway') this._dialogs.openDelete('gateway', target.gatewayKey);
    else this._deleteTransition(target.transitionIndex);
  }

  /** The thing a menu target points at, with the name it is announced by. */
  private _describe(target: NodeTarget): { value: unknown; name: string } | null {
    const blueprint = this.serviceBlueprint;
    if (target.kind === 'stage') {
      const stage = blueprint?.stages.find((candidate) => candidate.stageKey === target.stageKey);
      return stage ? { value: stage, name: stage.displayName } : null;
    }
    if (target.kind === 'gateway') {
      const gateway = serviceBlueprintGateways(blueprint).find((candidate) => candidate.key === target.gatewayKey);
      return gateway ? { value: gateway, name: gateway.displayName } : null;
    }
    const transition = flattenRoutes(blueprint)[target.transitionIndex];
    return transition ? { value: transition, name: `Transition “${transition.action}”` } : null;
  }

  private async _copyJson(target: NodeTarget) {
    const described = this._describe(target);
    if (!described) {
      return;
    }

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(JSON.stringify(described.value, null, 2));
      }
      this._announce(`${described.name} copied.`);
    } catch {
      this._announce(`${described.name} copy prepared, but clipboard access was unavailable.`);
    }
  }

  // ── public workspace actions ──────────────────────────────────────────────
  //
  // Called by this element's own HUD (standalone use, hideOwnToolbar false) or directly by a host's
  // consolidated toolbar (hideOwnToolbar true; see wayfinder-service-blueprint-editor.ts). Plain
  // methods rather than events, since a host toolbar button needs to *trigger* these, not just react.

  addStage(returnTarget?: HTMLElement | null) {
    this._openAddStage(this._selection.stageKey, this._selection.stageKey ? 'after' : 'append', returnTarget);
  }

  addGateway(returnTarget?: HTMLElement | null) {
    this._dialogs.openCreateGateway(returnTarget);
  }

  /** Opens "add stage", sitting after `referenceStageKey` when it is given and `position` is 'after'. */
  private _openAddStage(referenceStageKey: string | null, position: 'after' | 'append', returnTarget?: HTMLElement | null) {
    const reference = this.serviceBlueprint?.stages.find((stage) => stage.stageKey === referenceStageKey) ?? null;
    const surface: StageSurface = reference ? stageSurface(reference) : 'front-stage';
    this._dialogs.openCreateStage(
      surface,
      position === 'after' && referenceStageKey ? { position: 'after', referenceStageKey } : { position: 'append' },
      returnTarget
    );
  }

  tidyLayout() {
    if (!this.serviceBlueprint) {
      return;
    }
    this._emitUpdated(applyAutoArrange(this.serviceBlueprint, this.availableQueues), this._selection.detail);
    this._announce('Canvas tidied — nodes returned to the automatic layout.');
    requestAnimationFrame(() => this._canvas.fitView());
  }

  zoomIn() {
    this._canvas.zoomIn();
  }

  zoomOut() {
    this._canvas.zoomOut();
  }

  fitToWidth() {
    this._canvas.fitWidth();
    this._announce('Canvas fit to the diagram’s width.');
  }

  fitToScreen() {
    this._canvas.fitView();
    this._announce('Canvas fit to screen.');
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private _emitUpdated(serviceBlueprint: ServiceBlueprint, selection?: GraphSelectionDetail | null) {
    this.serviceBlueprint = serviceBlueprint;
    this.dispatchEvent(
      new CustomEvent<ServiceBlueprintUpdatedDetail>('service-blueprint-updated', {
        detail: { serviceBlueprint, selection },
        bubbles: true,
        composed: true,
      })
    );
  }

  private _labelFor(nodeKey: string): string {
    return (
      this.serviceBlueprint?.stages.find((stage) => stage.stageKey === nodeKey)?.displayName ??
      this.serviceBlueprint?.gateways?.find((gateway) => gateway.key === nodeKey)?.displayName ??
      nodeKey
    );
  }

  private _queueLabel(queueKey: string) {
    return stageQueueLabel(this.serviceBlueprint, queueKey, this.availableQueues);
  }

  private _announce(message: string) {
    const announcer = this.shadowRoot?.getElementById('graph-announcer');
    if (!announcer) {
      return;
    }

    announcer.textContent = '';
    requestAnimationFrame(() => {
      announcer.textContent = message;
    });
  }

  // ── render ────────────────────────────────────────────────────────────────

  private _renderGraph() {
    const isEmpty = (this.serviceBlueprint?.stages.length ?? 0) === 0 && serviceBlueprintGateways(this.serviceBlueprint).length === 0;
    return html`
      ${this.hideOwnToolbar ? nothing : renderGraphHud(this, Math.round(this._zoom * 100), this.readOnly)}
      ${
        isEmpty
          ? renderWorkspaceEmptyState(this.readOnly, (trigger) =>
              this._dialogs.openCreateStage('front-stage', { position: 'append' }, trigger)
            )
          : html`<div
            class="graph-canvas"
            role="application"
            tabindex="0"
            aria-label=${`Service blueprint graph canvas — ${this.serviceBlueprint?.displayName ?? 'service blueprint'}`}
            aria-roledescription=${this.readOnly ? 'Service blueprint graph viewer' : 'Service blueprint graph editor'}
            @click=${() => this._menu.dismiss(false)}
          >
            <div class="graph-react-host" data-wayfinder-component="service-blueprint-graph" data-wayfinder-mode="graph"></div>
          </div>`
      }
    `;
  }

  render() {
    return html`
      <div class="service-blueprint-graph-root" data-wayfinder-component="service-blueprint-graph" data-wayfinder-mode="graph" data-wayfinder-read-only=${String(this.readOnly)}>
        <div id="graph-announcer" role="status" aria-live="polite" aria-atomic="true" class="sr-only"></div>

        ${this._renderGraph()}
        ${this.readOnly ? nothing : this._menu.render()}
        ${this.readOnly ? nothing : this._dialogs.render()}
      </div>
    `;
  }

  static styles = unsafeCSS(graphStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-service-blueprint-graph': WayfinderServiceBlueprintGraphElement;
  }
}
