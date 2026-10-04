import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ServiceBlueprintGatewayDefinition, StageDefinition, ServiceBlueprint } from './types.js';
import {
  applyQueueToStage,
  stageQueueLabel,
  stageSurface,
  type StageSurface,
  type QueueDefinition,
  serviceBlueprintQueueOptions,
} from './stage-assignment.js';
import { serviceBlueprintGateways } from './types.js';
import { gatewayQueueKey } from './gateway-representation.js';
import { addRoute, buildRoute, deleteRoute, findOrCreateSplitGateway, flattenRoutes } from './route-model.js';
import type { GraphBridge } from './graph/graph-bridge.js';
import type { GraphCallbacks, GraphNodeMove, GraphProps } from './graph/graph-callbacks.js';
import { gatewayNodeId, parseGraphNodeId, stageNodeId } from './graph/service-blueprint-graph-layout.js';
import { applyAutoArrange, setNodePositions, setRouteWaypoint } from './graph/service-blueprint-graph-layout-block.js';
import { GraphDialogsController } from './graph/graph-dialogs-controller.js';
import graphStyles from './wayfinder-service-blueprint-graph.css?inline';

type SelectionKind = 'stage' | 'transition' | 'gateway';

type GraphSelectionDetail = {
  kind: SelectionKind;
  stageKey?: string;
  transitionIndex?: number;
  gatewayKey?: string;
};

type ServiceBlueprintUpdatedDetail = {
  serviceBlueprint: ServiceBlueprint;
  selection?: GraphSelectionDetail | null;
};

type ContextMenuTarget =
  | { kind: 'canvas' }
  | { kind: 'stage'; stageKey: string }
  | { kind: 'gateway'; gatewayKey: string }
  | { kind: 'transition'; transitionIndex: number };

type ContextMenuState = ContextMenuTarget & {
  x: number;
  y: number;
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
  private _selectedStageKey: string | null = null;

  @state()
  private _selectedTransitionIndex: number | null = null;

  @state()
  private _selectedGatewayKey: string | null = null;

  @state()
  private _zoom = 1;

  @state()
  private _contextMenu: ContextMenuState | null = null;

  private _contextReturnTarget: HTMLElement | null = null;
  private readonly _dialogs = new GraphDialogsController(this, {
    blueprint: () => this.serviceBlueprint,
    queueKeys: () => this._availableQueueKeys(),
    queueLabel: (queueKey) => this._roleLabelForQueue(queueKey),
    nodeLabel: (nodeKey) => this._labelForStage(nodeKey),
    fallbackReturnTarget: () => this._contextReturnTarget,
    dismissContextMenu: () => this._dismissContextMenu(false),
    announce: (message) => this._announce(message),
    stageCreated: (blueprint, stageKey) => {
      this._selectedStageKey = stageKey;
      this._selectedTransitionIndex = null;
      this._emitSelectionChange({ kind: 'stage', stageKey });
      this._emitServiceBlueprintUpdated(blueprint, { kind: 'stage', stageKey });
      this._requestInspector({ kind: 'stage', stageKey });
      // A new stage has no routes to anchor it near existing content — bring it into view.
      requestAnimationFrame(() => this._bridge?.centerOnNode(stageNodeId(stageKey)));
    },
    gatewayCreated: (blueprint, gatewayKey) => {
      this._emitServiceBlueprintUpdated(blueprint, { kind: 'gateway', gatewayKey });
      requestAnimationFrame(() => this._bridge?.centerOnNode(gatewayNodeId(gatewayKey)));
    },
    nodeDeleted: (blueprint, node) => {
      if (node === 'stage') {
        this._selectedStageKey = null;
      } else {
        this._selectedGatewayKey = null;
      }
      this._selectedTransitionIndex = null;
      this._emitServiceBlueprintUpdated(blueprint, null);
    },
  });
  private _statusTimer: number | null = null;
  private _bridge: GraphBridge | null = null;
  private _bridgeHost: HTMLElement | null = null;
  private _bridgeLoading = false;
  private _lastMultiSelection: string[] = [];

  connectedCallback() {
    super.connectedCallback();
  }

  disconnectedCallback() {
    if (this._statusTimer !== null) {
      window.clearTimeout(this._statusTimer);
      this._statusTimer = null;
    }
    this._teardownGraphCanvas();
    super.disconnectedCallback();
  }

  protected updated(changed: Map<string, unknown>) {
    if (changed.has('serviceBlueprintJson') && this.serviceBlueprintJson) {
      try {
        const parsed = JSON.parse(this.serviceBlueprintJson) as ServiceBlueprint;
        this.serviceBlueprint = parsed;
      } catch (error) {
        console.error('wayfinder-service-blueprint-graph: service-blueprint-json could not be parsed.', error);
      }
    }

    if (changed.has('selectedStageKey')) {
      this._selectedStageKey = this.selectedStageKey ?? null;
    }

    if (changed.has('selectedTransitionIndex')) {
      this._selectedTransitionIndex = this.selectedTransitionIndex ?? null;
    }

    if (changed.has('selectedGatewayKey')) {
      this._selectedGatewayKey = this.selectedGatewayKey ?? null;
    }

    const stages = this.serviceBlueprint?.stages ?? [];
    const transitions = flattenRoutes(this.serviceBlueprint);
    const gateways = this.serviceBlueprint?.gateways ?? [];

    if (this._selectedStageKey && !stages.some((stage) => stage.stageKey === this._selectedStageKey)) {
      this._selectedStageKey = null;
    }

    if (
      this._selectedTransitionIndex !== null &&
      (this._selectedTransitionIndex < 0 || this._selectedTransitionIndex >= transitions.length)
    ) {
      this._selectedTransitionIndex = null;
    }

    if (this._selectedGatewayKey && !gateways.some((gateway) => gateway.key === this._selectedGatewayKey)) {
      this._selectedGatewayKey = null;
    }

    this._syncGraphCanvas();
  }

  private _lastSnapshot: GraphProps | null = null;

  private _graphSnapshot(): GraphProps {
    // Hosts may recreate array props on every render (e.g. mapping the
    // simulation history inline). Reuse the previous reference when the
    // contents are unchanged so the React canvas only re-renders — and
    // re-seeds its local node state — on genuine changes.
    const previous = this._lastSnapshot;
    const stable = <T>(next: T[], prior: T[] | undefined): T[] =>
      prior && prior.length === next.length && next.every((value, index) => value === prior[index]) ? prior : next;
    const snapshot: GraphProps = {
      serviceBlueprint: this.serviceBlueprint,
      availableQueues: stable(this.availableQueues, previous?.availableQueues),
      readOnly: this.readOnly,
      selectedStageKey: this._selectedStageKey,
      selectedGatewayKey: this._selectedGatewayKey,
      selectedTransitionIndex: this._selectedTransitionIndex,
      simulationCurrentStageKey: this.simulationCurrentStageKey,
      simulationPathStageKeys: stable(this.simulationPathStageKeys, previous?.simulationPathStageKeys),
      simulationPathTransitionIndices: stable(this.simulationPathTransitionIndices, previous?.simulationPathTransitionIndices),
    };
    this._lastSnapshot = snapshot;
    return snapshot;
  }

  private _graphCallbacks(): GraphCallbacks {
    return {
      selectStage: (stageKey, options) => this._selectStage(stageKey, options),
      selectGateway: (gatewayKey, options) => this._selectGateway(gatewayKey, options),
      selectTransition: (transitionIndex, options) => this._selectTransition(transitionIndex, options),
      requestDeleteStage: (stageKey, returnTarget) => {
        if (!this.readOnly) {
          this._dialogs.openDelete('stage', stageKey, returnTarget);
        }
      },
      requestDeleteGateway: (gatewayKey, returnTarget) => {
        if (!this.readOnly) {
          this._dialogs.openDelete('gateway', gatewayKey, returnTarget);
        }
      },
      requestDeleteTransition: (transitionIndex) => {
        if (!this.readOnly) {
          this._deleteTransition(transitionIndex);
        }
      },
      openContextMenu: (position, target, returnTarget) => {
        if (!this.readOnly) {
          this._openContextMenu(position, target, returnTarget);
        }
      },
      paneClicked: () => this._dismissContextMenu(false),
      nodesMoved: (moves) => this._handleNodesMoved(moves),
      routeWaypointMoved: (edgeKey, position) => this._handleRouteWaypointMoved(edgeKey, position),
      connectRequested: (connection) => this._handleConnectRequested(connection),
      multiSelectionChanged: (nodeIds) => {
        // React Flow reports selection with a fresh array identity on every
        // render — only forward genuine changes or the host re-render loops.
        const unchanged =
          nodeIds.length === this._lastMultiSelection.length && nodeIds.every((id, index) => id === this._lastMultiSelection[index]);
        if (unchanged) {
          return;
        }
        this._lastMultiSelection = nodeIds;
        this.dispatchEvent(
          new CustomEvent<{ nodeIds: string[] }>('graph-multi-selection', {
            detail: { nodeIds },
            bubbles: true,
            composed: true,
          })
        );
      },
      laneFocused: (lane) =>
        this._announce(`${lane.label} queue. ${lane.stageCount} stage${lane.stageCount === 1 ? '' : 's'}. ${lane.description}.`),
      zoomChanged: (zoom) => {
        this._zoom = Number(zoom.toFixed(2));
        // Relayed so a host rendering its own consolidated toolbar (hideOwnToolbar) can show
        // the current zoom percentage without needing this element's own hidden HUD.
        this.dispatchEvent(
          new CustomEvent<{ zoom: number }>('zoom-changed', {
            detail: { zoom: this._zoom },
            bubbles: true,
            composed: true,
          })
        );
      },
      ready: () => this.setAttribute('data-wayfinder-graph-ready', 'true'),
    };
  }

  /**
   * Mount/refresh/unmount the React Flow canvas against the .graph-react-host
   * element the Lit template renders. The React bundle (react, react-dom,
   * @xyflow/react) loads lazily on first mount so definition-only usage never
   * downloads it — mirroring how CodeMirror is deferred.
   */
  private _syncGraphCanvas() {
    const host = this.shadowRoot?.querySelector<HTMLElement>('.graph-react-host') ?? null;
    if (!host) {
      this._teardownGraphCanvas();
      return;
    }

    if (this._bridge && this._bridgeHost === host) {
      this._bridge.update(this._graphSnapshot());
      return;
    }

    if (this._bridge) {
      this._teardownGraphCanvas();
    }

    if (this._bridgeLoading) {
      return;
    }
    this._bridgeLoading = true;
    void (async () => {
      try {
        const [{ GraphBridge: GraphBridgeCtor }, { graphStyleSheets }] = await Promise.all([
          import('./graph/graph-bridge.js'),
          import('./graph/graph-styles.js'),
        ]);
        const root = this.shadowRoot;
        if (!this.isConnected || !root) {
          return;
        }
        for (const sheet of graphStyleSheets()) {
          if (!root.adoptedStyleSheets.includes(sheet)) {
            root.adoptedStyleSheets = [...root.adoptedStyleSheets, sheet];
          }
        }
        const currentHost = root.querySelector<HTMLElement>('.graph-react-host');
        if (!currentHost) {
          return;
        }
        this._bridgeHost = currentHost;
        this._bridge = new GraphBridgeCtor(currentHost, this._graphSnapshot(), this._graphCallbacks());
      } finally {
        this._bridgeLoading = false;
      }
    })();
  }

  private _teardownGraphCanvas() {
    this._bridge?.unmount();
    this._bridge = null;
    this._bridgeHost = null;
    this.removeAttribute('data-wayfinder-graph-ready');
  }

  private _currentSelectionDetail(): GraphSelectionDetail | null {
    if (this._selectedStageKey) {
      return { kind: 'stage', stageKey: this._selectedStageKey };
    }
    if (this._selectedGatewayKey) {
      return { kind: 'gateway', gatewayKey: this._selectedGatewayKey };
    }
    if (this._selectedTransitionIndex !== null) {
      return { kind: 'transition', transitionIndex: this._selectedTransitionIndex };
    }
    return null;
  }

  private _handleNodesMoved(moves: GraphNodeMove[]) {
    if (this.readOnly || !this.serviceBlueprint || moves.length === 0) {
      return;
    }

    let next = setNodePositions(this.serviceBlueprint, Object.fromEntries(moves.map((move) => [move.nodeId, { x: move.x, y: move.y }])));

    const queueMoves = moves.filter((move) => move.queueKey);
    for (const move of queueMoves) {
      const parsed = parseGraphNodeId(move.nodeId);
      if (parsed.kind === 'stage') {
        next = {
          ...next,
          stages: next.stages.map((stage) => (stage.stageKey === parsed.key ? applyQueueToStage(stage, move.queueKey!) : stage)),
        };
      } else {
        next = {
          ...next,
          gateways: serviceBlueprintGateways(next).map((gateway) =>
            gateway.key === parsed.key ? { ...gateway, queueKey: move.queueKey!, actor: move.queueKey! } : gateway
          ),
        };
      }
    }

    this._emitServiceBlueprintUpdated(next, this._currentSelectionDetail());

    if (queueMoves.length > 0) {
      const first = queueMoves[0];
      this._announce(
        `${this._labelForStage(parseGraphNodeId(first.nodeId).key)} moved to the ${this._roleLabelForQueue(first.queueKey!)} queue.`
      );
    } else if (moves.length === 1) {
      this._announce(`${this._labelForStage(parseGraphNodeId(moves[0].nodeId).key)} moved.`);
    } else {
      this._announce(`${moves.length} nodes moved.`);
    }
  }

  private _handleRouteWaypointMoved(edgeKey: string, position: { x: number; y: number } | null) {
    if (this.readOnly || !this.serviceBlueprint) {
      return;
    }
    const next = setRouteWaypoint(this.serviceBlueprint, edgeKey, position);
    this._emitServiceBlueprintUpdated(next, this._currentSelectionDetail());
    this._announce(position ? 'Route bend point moved.' : 'Route reset to its automatic path.');
  }

  /**
   * Drag-to-connect. The gateway-routing invariant is preserved by
   * construction: state→state connections are routed through the source's
   * Split gateway (created on demand); state routes may target gateways
   * directly; gateway routes may target anything.
   */
  private _handleConnectRequested(connection: { sourceId: string; targetId: string }) {
    if (this.readOnly || !this.serviceBlueprint) {
      return;
    }
    const source = parseGraphNodeId(connection.sourceId);
    const target = parseGraphNodeId(connection.targetId);
    if (source.key === target.key) {
      return;
    }

    let serviceBlueprint = this.serviceBlueprint;
    let ownerKey = source.key;

    if (source.kind === 'stage' && target.kind === 'stage') {
      const ensured = findOrCreateSplitGateway(serviceBlueprint, source.key);
      serviceBlueprint = ensured.serviceBlueprint;
      ownerKey = ensured.gatewayKey;
    }

    const route = buildRoute({ source: ownerKey, target: target.key, trigger: 'continue' });
    if (flattenRoutes(serviceBlueprint).some((view) => view.routeId === route.id)) {
      this._announce(`A “continue” route from ${this._labelForStage(source.key)} to ${this._labelForStage(target.key)} already exists.`);
      return;
    }

    if (ownerKey === source.key && source.kind === 'stage') {
      serviceBlueprint = {
        ...serviceBlueprint,
        stages: serviceBlueprint.stages.map((stage) =>
          stage.stageKey === source.key ? { ...stage, routes: [...(stage.routes ?? []), route] } : stage
        ),
      };
    } else {
      serviceBlueprint = addRoute(serviceBlueprint, ownerKey, route);
    }

    const transitionIndex = flattenRoutes(serviceBlueprint).findIndex((view) => view.routeId === route.id);
    const selection: GraphSelectionDetail | null = transitionIndex >= 0 ? { kind: 'transition', transitionIndex } : null;
    if (selection) {
      this._selectedTransitionIndex = transitionIndex;
      this._selectedStageKey = null;
      this._selectedGatewayKey = null;
    }
    this._emitServiceBlueprintUpdated(serviceBlueprint, selection);
    if (selection) {
      this._emitSelectionChange(selection);
      this._requestInspector(selection);
    }
    this._announce(`Route added from ${this._labelForStage(source.key)} to ${this._labelForStage(target.key)}.`);
  }

  /**
   * Public workspace actions — called either by this element's own HUD (standalone use,
   * hideOwnToolbar false) or directly by a host's consolidated toolbar (hideOwnToolbar true;
   * see wayfinder-service-blueprint-editor.ts). Kept as plain public methods rather than
   * events, since a host toolbar button needs to *trigger* these, not just react to them.
   */
  addStage(returnTarget?: HTMLElement | null) {
    const selectedStage = this.serviceBlueprint?.stages.find((stage) => stage.stageKey === this._selectedStageKey) ?? null;
    this._dialogs.openCreateStage(
      selectedStage ? this._surfaceForStage(selectedStage) : 'front-stage',
      this._selectedStageKey ? { position: 'after', referenceStageKey: this._selectedStageKey } : { position: 'append' },
      returnTarget
    );
  }

  addGateway(returnTarget?: HTMLElement | null) {
    this._dialogs.openCreateGateway(returnTarget);
  }

  tidyLayout() {
    if (!this.serviceBlueprint) {
      return;
    }
    const next = applyAutoArrange(this.serviceBlueprint, this.availableQueues);
    this._emitServiceBlueprintUpdated(next, this._currentSelectionDetail());
    this._announce('Canvas tidied — nodes returned to the automatic layout.');
    requestAnimationFrame(() => this._bridge?.fitView());
  }

  zoomIn() {
    this._bridge?.zoomIn();
  }

  zoomOut() {
    this._bridge?.zoomOut();
  }

  fitToWidth() {
    this._bridge?.fitWidth();
    this._announce('Canvas fit to the diagram’s width.');
  }

  private _surfaceForStage(stage: StageDefinition): StageSurface {
    return stageSurface(stage);
  }

  private _queueKeyForGateway(gateway: ServiceBlueprintGatewayDefinition) {
    return gatewayQueueKey(gateway) || 'public';
  }

  private _roleLabelForQueue(queueKey: string) {
    return stageQueueLabel(this.serviceBlueprint, queueKey, this.availableQueues);
  }

  private _availableQueueKeys() {
    return serviceBlueprintQueueOptions(this.serviceBlueprint, this.availableQueues);
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

  private _selectStage(stageKey: string, options?: { openInspector?: boolean }) {
    this._selectedStageKey = stageKey;
    this._selectedTransitionIndex = null;
    this._selectedGatewayKey = null;

    this.dispatchEvent(
      new CustomEvent<{ stageKey: string }>('stage-selected', {
        detail: { stageKey },
        bubbles: true,
        composed: true,
      })
    );
    this._emitSelectionChange({ kind: 'stage', stageKey });
    this._announce(`Stage “${this._labelForStage(stageKey)}” selected.`);

    if (options?.openInspector) {
      this._requestInspector({ kind: 'stage', stageKey });
    }
  }

  private _selectGateway(gatewayKey: string, options?: { openInspector?: boolean }) {
    const gateway = this.serviceBlueprint?.gateways?.find((candidate) => candidate.key === gatewayKey);
    if (!gateway) {
      return;
    }

    this._selectedGatewayKey = gatewayKey;
    this._selectedStageKey = null;
    this._selectedTransitionIndex = null;

    this.dispatchEvent(
      new CustomEvent<{ gatewayKey: string }>('gateway-selected', {
        detail: { gatewayKey },
        bubbles: true,
        composed: true,
      })
    );
    this._emitSelectionChange({ kind: 'gateway', gatewayKey });
    this._announce(
      `Gateway “${gateway.displayName}” selected. ${gateway.gatewayType} gateway in the ${this._roleLabelForQueue(this._queueKeyForGateway(gateway))} queue.`
    );

    if (options?.openInspector) {
      this._requestInspector({ kind: 'gateway', gatewayKey });
    }
  }

  private _selectTransition(index: number, options?: { openInspector?: boolean }) {
    const transition = flattenRoutes(this.serviceBlueprint)[index];
    if (!transition) {
      return;
    }

    this._selectedTransitionIndex = index;
    this._selectedStageKey = null;
    this._selectedGatewayKey = null;

    this.dispatchEvent(
      new CustomEvent<{ transitionIndex: number }>('transition-selected', {
        detail: { transitionIndex: index },
        bubbles: true,
        composed: true,
      })
    );
    this._emitSelectionChange({ kind: 'transition', transitionIndex: index });
    this._announce(
      `Transition “${transition.action}” selected, from ${this._labelForStage(transition.fromStage)} to ${this._labelForStage(transition.toStage)}.`
    );

    if (options?.openInspector) {
      this._requestInspector({ kind: 'transition', transitionIndex: index });
    }
  }

  private _emitSelectionChange(detail: GraphSelectionDetail) {
    this.dispatchEvent(
      new CustomEvent<GraphSelectionDetail>('selection-change', {
        detail,
        bubbles: true,
        composed: true,
      })
    );
  }

  private _requestInspector(detail: GraphSelectionDetail) {
    this.dispatchEvent(
      new CustomEvent<GraphSelectionDetail>('inspector-requested', {
        detail,
        bubbles: true,
        composed: true,
      })
    );
  }

  private _emitServiceBlueprintUpdated(serviceBlueprint: ServiceBlueprint, selection?: GraphSelectionDetail | null) {
    this.serviceBlueprint = serviceBlueprint;
    this.dispatchEvent(
      new CustomEvent<ServiceBlueprintUpdatedDetail>('service-blueprint-updated', {
        detail: { serviceBlueprint, selection },
        bubbles: true,
        composed: true,
      })
    );
  }

  private _labelForStage(stageKey: string): string {
    return (
      this.serviceBlueprint?.stages.find((stage) => stage.stageKey === stageKey)?.displayName ??
      this.serviceBlueprint?.gateways?.find((gateway) => gateway.key === stageKey)?.displayName ??
      stageKey
    );
  }

  private async _copyGateway(gatewayKey: string) {
    const gateway = serviceBlueprintGateways(this.serviceBlueprint).find((candidate) => candidate.key === gatewayKey);
    if (!gateway) {
      return;
    }

    const payload = JSON.stringify(gateway, null, 2);
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(payload);
      }
      this._announce(`${gateway.displayName} copied.`);
    } catch {
      this._announce(`${gateway.displayName} copy prepared, but clipboard access was unavailable.`);
    }
    this._dismissContextMenu(false);
  }

  private async _copyStage(stageKey: string) {
    const stage = this.serviceBlueprint?.stages.find((candidate) => candidate.stageKey === stageKey);
    if (!stage) {
      return;
    }

    const payload = JSON.stringify(stage, null, 2);
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(payload);
      }
      this._announce(`${stage.displayName} copied.`);
    } catch {
      this._announce(`${stage.displayName} copy prepared, but clipboard access was unavailable.`);
    }
    this._dismissContextMenu(false);
  }

  private async _copyTransition(index: number) {
    const transition = flattenRoutes(this.serviceBlueprint)[index];
    if (!transition) {
      return;
    }

    const payload = JSON.stringify(transition, null, 2);
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(payload);
      }
      this._announce(`Transition “${transition.action}” copied.`);
    } catch {
      this._announce(`Transition “${transition.action}” copy prepared, but clipboard access was unavailable.`);
    }
    this._dismissContextMenu(false);
  }

  private _deleteTransition(index: number) {
    if (!this.serviceBlueprint) {
      return;
    }

    const transition = flattenRoutes(this.serviceBlueprint)[index];
    if (!transition) {
      return;
    }

    const gatewayKey = transition.key;
    const routeId = transition.routeId;
    if (!gatewayKey || !routeId) {
      return;
    }
    const serviceBlueprint: ServiceBlueprint = deleteRoute(this.serviceBlueprint, { gatewayKey, routeId });

    this._selectedTransitionIndex = null;
    this._emitServiceBlueprintUpdated(serviceBlueprint, null);
    this._dismissContextMenu(false);
    this._announce(`Transition “${transition.action}” deleted.`);
  }

  private _openContextMenu(position: { clientX: number; clientY: number }, target: ContextMenuTarget, returnTarget?: HTMLElement) {
    const hostRect = this.getBoundingClientRect();
    this._contextMenu = {
      ...target,
      x: Math.max(12, position.clientX - hostRect.left),
      y: Math.max(12, position.clientY - hostRect.top),
    };
    this._contextReturnTarget = returnTarget ?? null;

    requestAnimationFrame(() => {
      const menu = this.shadowRoot?.querySelector<HTMLElement>('[data-wayfinder-context-menu]');
      menu?.querySelector<HTMLButtonElement>('button')?.focus();
      if (menu && this._contextMenu) {
        const menuRect = menu.getBoundingClientRect();
        const margin = 12;
        const overflowX = menuRect.right - (hostRect.left + hostRect.width) + margin;
        const overflowY = menuRect.bottom - (hostRect.top + hostRect.height) + margin;
        if (overflowX > 0 || overflowY > 0) {
          this._contextMenu = {
            ...this._contextMenu,
            x: overflowX > 0 ? Math.max(margin, this._contextMenu.x - overflowX) : this._contextMenu.x,
            y: overflowY > 0 ? Math.max(margin, this._contextMenu.y - overflowY) : this._contextMenu.y,
          };
        }
      }
    });
  }

  private _dismissContextMenu(restoreFocus = true) {
    this._contextMenu = null;
    if (restoreFocus && this._contextReturnTarget) {
      requestAnimationFrame(() => this._contextReturnTarget?.focus());
    }
    this._contextReturnTarget = null;
  }

  private _handleContextMenuAction(action: string) {
    const target = this._contextMenu;
    if (!target) {
      return;
    }

    if (action === 'fit-screen') {
      this.fitToScreen();
      this._dismissContextMenu(false);
      return;
    }

    if (action === 'add-stage') {
      const referenceStageKey = target.kind === 'stage' ? target.stageKey : this._selectedStageKey;
      const referenceStage = referenceStageKey
        ? (this.serviceBlueprint?.stages.find((stage) => stage.stageKey === referenceStageKey) ?? null)
        : null;
      this._dialogs.openCreateStage(
        referenceStage ? this._surfaceForStage(referenceStage) : 'front-stage',
        target.kind === 'stage' ? { position: 'after', referenceStageKey: target.stageKey } : { position: 'append' }
      );
      return;
    }

    if (target.kind === 'stage') {
      if (action === 'copy-stage') {
        void this._copyStage(target.stageKey);
      } else if (action === 'delete-stage') {
        this._dialogs.openDelete('stage', target.stageKey);
      } else if (action === 'edit-stage') {
        this._selectStage(target.stageKey, { openInspector: true });
        this._dismissContextMenu(false);
      }
      return;
    }

    if (target.kind === 'gateway') {
      if (action === 'copy-gateway') {
        void this._copyGateway(target.gatewayKey);
      } else if (action === 'delete-gateway') {
        this._dialogs.openDelete('gateway', target.gatewayKey);
      } else if (action === 'edit-gateway') {
        this._selectGateway(target.gatewayKey, { openInspector: true });
        this._dismissContextMenu(false);
      }
      return;
    }

    if (target.kind === 'transition') {
      if (action === 'copy-transition') {
        void this._copyTransition(target.transitionIndex);
      } else if (action === 'delete-transition') {
        this._deleteTransition(target.transitionIndex);
      } else if (action === 'edit-transition') {
        this._selectTransition(target.transitionIndex, { openInspector: true });
        this._dismissContextMenu(false);
      }
    }
  }

  private _renderContextMenu() {
    const target = this._contextMenu;
    if (!target) {
      return nothing;
    }

    return html`
      <div
        class="context-menu"
        style=${`left:${target.x}px;top:${target.y}px;`}
        role="menu"
        aria-label="Graph workspace actions"
        data-wayfinder-context-menu
        @keydown=${(event: KeyboardEvent) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            this._dismissContextMenu();
          }
        }}
        @click=${(event: Event) => event.stopPropagation()}
      >
        ${
          target.kind !== 'transition'
            ? html`
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('add-stage')}>
                Add stage
              </button>
            `
            : nothing
        }
        ${
          target.kind === 'canvas'
            ? html`
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('fit-screen')}>
                Fit to screen
              </button>
            `
            : nothing
        }
        ${
          target.kind === 'stage'
            ? html`
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('edit-stage')}>
                Open stage inspector
              </button>
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('copy-stage')}>
                Copy stage JSON
              </button>
              <button type="button" role="menuitem" class="danger" @click=${() => this._handleContextMenuAction('delete-stage')}>
                Delete stage
              </button>
            `
            : nothing
        }
        ${
          target.kind === 'gateway'
            ? html`
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('edit-gateway')}>
                Open gateway inspector
              </button>
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('copy-gateway')}>
                Copy gateway JSON
              </button>
              <button type="button" role="menuitem" class="danger" @click=${() => this._handleContextMenuAction('delete-gateway')}>
                Delete gateway
              </button>
            `
            : nothing
        }
        ${
          target.kind === 'transition'
            ? html`
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('edit-transition')}>
                Open transition inspector
              </button>
              <button type="button" role="menuitem" @click=${() => this._handleContextMenuAction('copy-transition')}>
                Copy transition JSON
              </button>
              <button type="button" role="menuitem" class="danger" @click=${() => this._handleContextMenuAction('delete-transition')}>
                Delete transition
              </button>
            `
            : nothing
        }
      </div>
    `;
  }

  private _renderGraph() {
    const stages = this.serviceBlueprint?.stages ?? [];
    const gateways = serviceBlueprintGateways(this.serviceBlueprint);
    const isEmpty = stages.length === 0 && gateways.length === 0;

    return html`
      ${
        this.hideOwnToolbar
          ? nothing
          : html`
            <div class="graph-hud" aria-label="Workspace controls and hints">
              ${
                this.readOnly
                  ? nothing
                  : html`
                    <div class="hud-group">
                      <button
                        type="button"
                        class="hud-button hud-button--icon"
                        data-wayfinder-add-stage
                        aria-label="Add stage"
                        title="Add stage"
                        @click=${(event: Event) => this.addStage(event.currentTarget as HTMLElement)}
                      >
                        <span aria-hidden="true">▭+</span>
                      </button>
                      <button
                        type="button"
                        class="hud-button hud-button--icon"
                        data-wayfinder-add-gateway
                        aria-label="Add gateway"
                        title="Add gateway"
                        @click=${(event: Event) => this.addGateway(event.currentTarget as HTMLElement)}
                      >
                        <span aria-hidden="true">◇+</span>
                      </button>
                      <button
                        type="button"
                        class="hud-button hud-button--icon"
                        data-wayfinder-auto-arrange
                        aria-label="Tidy layout"
                        title="Tidy layout"
                        @click=${() => this.tidyLayout()}
                      >
                        <span aria-hidden="true">▦</span>
                      </button>
                    </div>
                  `
              }
              <div class="hud-group">
                <button type="button" class="hud-button hud-button--icon" aria-label="Zoom out" title="Zoom out" @click=${() => this.zoomOut()}>
                  <span aria-hidden="true">−</span>
                </button>
                <span class="zoom-indicator" data-wayfinder-zoom>${Math.round(this._zoom * 100)}%</span>
                <button type="button" class="hud-button hud-button--icon" aria-label="Zoom in" title="Zoom in" @click=${() => this.zoomIn()}>
                  <span aria-hidden="true">+</span>
                </button>
                <button type="button" class="hud-button hud-button--icon" data-wayfinder-fit-screen aria-label="Fit to screen" title="Fit to screen" @click=${() => this.fitToScreen()}>
                  <span aria-hidden="true">⛶</span>
                </button>
                <button type="button" class="hud-button hud-button--icon" data-wayfinder-fit-width aria-label="Fit width" title="Fit width" @click=${() => this.fitToWidth()}>
                  <span aria-hidden="true">↔</span>
                </button>
              </div>
            </div>
          `
      }

      ${
        isEmpty
          ? this._renderWorkspaceEmptyState()
          : html`<div
            class="graph-canvas"
            role="application"
            tabindex="0"
            aria-label=${`Service blueprint graph canvas — ${this.serviceBlueprint?.displayName ?? 'service blueprint'}`}
            aria-roledescription=${this.readOnly ? 'Service blueprint graph viewer' : 'Service blueprint graph editor'}
            @click=${() => this._dismissContextMenu(false)}
          >
            <div class="graph-react-host" data-wayfinder-component="service-blueprint-graph" data-wayfinder-mode="graph"></div>
          </div>`
      }
    `;
  }

  fitToScreen() {
    this._bridge?.fitView();
    this._announce('Canvas fit to screen.');
  }

  private _renderWorkspaceEmptyState() {
    return html`
      <section class="workspace-empty-state" role="status" data-wayfinder-empty-state="graph">
        <h2 class="workspace-empty-title">${this.readOnly ? 'No stages to display' : 'Start building your service blueprint'}</h2>
        <p class="workspace-empty-copy">
          ${
            this.readOnly
              ? 'This serviceBlueprint has no stages.'
              : 'This serviceBlueprint does not have any stages yet. Add the first stage, then connect routes as you model the author journey.'
          }
        </p>
        ${
          this.readOnly
            ? nothing
            : html`
              <ul class="workspace-empty-tips">
                <li>Use <strong>Add stage</strong>, then choose the queue that should own the work.</li>
                <li><strong>Add the next stage before you branch</strong> — gateways always connect existing stages, never empty space.</li>
                <li>Use the editor Help button or press <strong>F1</strong> to review shortcuts while you work.</li>
              </ul>
              <div class="workspace-empty-actions">
                <button
                  type="button"
                  class="hud-button"
                  data-wayfinder-empty-add-stage
                  @click=${(event: Event) => this._dialogs.openCreateStage('front-stage', { position: 'append' }, event.currentTarget as HTMLElement)}
                >
                  Add first stage
                </button>
              </div>
            `
        }
      </section>
    `;
  }

  render() {
    return html`
      <div class="service-blueprint-graph-root" data-wayfinder-component="service-blueprint-graph" data-wayfinder-mode="graph" data-wayfinder-read-only=${String(this.readOnly)}>
        <div id="graph-announcer" role="status" aria-live="polite" aria-atomic="true" class="sr-only"></div>

        ${this._renderGraph()}
        ${this.readOnly ? nothing : this._renderContextMenu()}
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
