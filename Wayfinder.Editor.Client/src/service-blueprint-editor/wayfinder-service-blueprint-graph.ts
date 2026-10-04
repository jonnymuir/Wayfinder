import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ServiceBlueprintGatewayDefinition, StageDefinition, StageKind, RouteView, ServiceBlueprint } from './types.js';
import { STAGE_KIND_OPTIONS } from './stage-kind-options.js';
import {
  applyQueueToStage,
  stageQueueKey,
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
import { applyAutoArrange, pruneLayout, setNodePositions, setRouteWaypoint } from './graph/service-blueprint-graph-layout-block.js';
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

type CreateStageDialogState = {
  surfaceHint: StageSurface;
  position: 'append' | 'before' | 'after';
  referenceStageKey: string | null;
  title: string;
  stageKey: string;
  queueKey: string;
  stageType: StageKind;
  keyTouched: boolean;
  error: string | null;
};

type DeleteStageDialogState = {
  stageKey: string;
  affectedTransitions: RouteView[];
};

type DeleteGatewayDialogState = {
  gatewayKey: string;
  affectedTransitions: RouteView[];
};

type CreateGatewayDialogState = {
  title: string;
  gatewayKey: string;
  kind: 'Split' | 'Join';
  queueKey: string;
  keyTouched: boolean;
  error: string | null;
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

  @state()
  private _createStageDialog: CreateStageDialogState | null = null;

  @state()
  private _deleteStageDialog: DeleteStageDialogState | null = null;

  @state()
  private _createGatewayDialog: CreateGatewayDialogState | null = null;

  @state()
  private _deleteGatewayDialog: DeleteGatewayDialogState | null = null;

  private _contextReturnTarget: HTMLElement | null = null;
  private _statusTimer: number | null = null;
  private _dialogReturnTarget: HTMLElement | null = null;
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
          this._openDeleteStageDialog(stageKey, returnTarget ?? null);
        }
      },
      requestDeleteGateway: (gatewayKey, returnTarget) => {
        if (!this.readOnly) {
          this._openDeleteGatewayDialog(gatewayKey, returnTarget ?? null);
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
    this._openCreateStageDialog(
      selectedStage ? this._surfaceForStage(selectedStage) : 'front-stage',
      this._selectedStageKey ? 'after' : 'append',
      this._selectedStageKey,
      returnTarget
    );
  }

  addGateway(returnTarget?: HTMLElement | null) {
    this._openCreateGatewayDialog(returnTarget);
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

  private _makeUniqueStageKey(base: string) {
    const usedKeys = new Set(this.serviceBlueprint?.stages.map((stage) => stage.stageKey) ?? []);
    let candidate = base;
    let suffix = 2;
    while (usedKeys.has(candidate)) {
      candidate = `${base}-${suffix}`;
      suffix += 1;
    }
    return candidate;
  }

  private _slugifyStageKey(value: string, fallback: string) {
    const slug =
      value
        .trim()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '') || fallback;
    return this._makeUniqueStageKey(slug);
  }

  private _defaultQueueForSurface(surface: StageSurface) {
    return surface === 'back-stage' ? 'reviewer' : 'public';
  }

  private _openCreateStageDialog(
    surfaceHint: StageSurface,
    position: 'append' | 'before' | 'after',
    referenceStageKey: string | null,
    returnTarget?: HTMLElement | null
  ) {
    const referenceStage = referenceStageKey
      ? (this.serviceBlueprint?.stages.find((stage) => stage.stageKey === referenceStageKey) ?? null)
      : null;
    const defaultQueueKey = referenceStage ? stageQueueKey(referenceStage) : this._defaultQueueForSurface(surfaceHint);
    const baseTitle = 'New stage';
    this._dialogReturnTarget = returnTarget ?? this._contextReturnTarget ?? null;
    this._createStageDialog = {
      surfaceHint,
      position,
      referenceStageKey,
      title: baseTitle,
      stageKey: this._slugifyStageKey(baseTitle, 'new-stage'),
      queueKey: defaultQueueKey,
      stageType: 'Question',
      keyTouched: false,
      error: null,
    };
    this._dismissContextMenu(false);
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLInputElement>('[data-wayfinder-create-stage-title]')?.focus();
    });
  }

  private _updateCreateStageTitle(value: string) {
    if (!this._createStageDialog) {
      return;
    }

    this._createStageDialog = {
      ...this._createStageDialog,
      title: value,
      stageKey: this._createStageDialog.keyTouched ? this._createStageDialog.stageKey : this._slugifyStageKey(value, 'new-stage'),
      error: null,
    };
  }

  private _updateCreateStageKey(value: string) {
    if (!this._createStageDialog) {
      return;
    }

    this._createStageDialog = {
      ...this._createStageDialog,
      stageKey: value,
      keyTouched: true,
      error: null,
    };
  }

  private _updateCreateStageQueue(value: string) {
    if (!this._createStageDialog) {
      return;
    }

    const previewStage = applyQueueToStage(
      {
        stageKey: '',
        displayName: '',
        queueKey: '',
        roleGates: [],
        actions: [],
        components: [],
      },
      value
    );

    this._createStageDialog = {
      ...this._createStageDialog,
      queueKey: value,
      surfaceHint: stageSurface(previewStage),
      error: null,
    };
  }

  private _closeCreateStageDialog() {
    this._createStageDialog = null;
    const returnTarget = this._dialogReturnTarget;
    this._dialogReturnTarget = null;
    requestAnimationFrame(() => returnTarget?.focus());
  }

  private _submitCreateStage() {
    if (!this.serviceBlueprint || !this._createStageDialog) {
      return;
    }

    const dialog = this._createStageDialog;
    const title = dialog.title.trim();
    const stageKey = dialog.stageKey.trim().toLowerCase();
    if (!title) {
      this._createStageDialog = { ...this._createStageDialog, error: 'Stage name is required.' };
      return;
    }

    if (!stageKey) {
      this._createStageDialog = { ...this._createStageDialog, error: 'Stage key is required.' };
      return;
    }

    if (this.serviceBlueprint.stages.some((stage) => stage.stageKey === stageKey)) {
      this._createStageDialog = { ...this._createStageDialog, error: 'Stage key must be unique.' };
      return;
    }

    const newStage = applyQueueToStage(
      {
        stageKey: stageKey,
        displayName: title,
        components: [],
        stageType: dialog.stageType,
        queueKey: '',
        actions: [],
        roleGates: [],
      },
      dialog.queueKey
    );

    const stages = [...this.serviceBlueprint.stages];
    let insertIndex = stages.length;
    if (dialog.referenceStageKey) {
      const referenceIndex = stages.findIndex((stage) => stage.stageKey === dialog.referenceStageKey);
      if (referenceIndex >= 0) {
        insertIndex = dialog.position === 'before' ? referenceIndex : referenceIndex + 1;
      }
    }
    stages.splice(insertIndex, 0, newStage);

    const serviceBlueprint: ServiceBlueprint = {
      ...this.serviceBlueprint,
      initialStage: this.serviceBlueprint.initialStage || newStage.stageKey,
      stages: stages,
    };

    this._selectedStageKey = newStage.stageKey;
    this._selectedTransitionIndex = null;
    this._emitSelectionChange({ kind: 'stage', stageKey: newStage.stageKey });
    this._emitServiceBlueprintUpdated(serviceBlueprint, { kind: 'stage', stageKey: newStage.stageKey });
    this._requestInspector({ kind: 'stage', stageKey: newStage.stageKey });
    this._announce(`${newStage.displayName} added to the workspace.`);
    this._closeCreateStageDialog();
    // New stage starts with no routes, so nothing anchors it near existing
    // content — pan/zoom to it so the author can see where it actually
    // landed instead of hunting for it off-viewport.
    requestAnimationFrame(() => this._bridge?.centerOnNode(stageNodeId(newStage.stageKey)));
  }

  private _openCreateGatewayDialog(returnTarget?: HTMLElement | null) {
    if (!this.serviceBlueprint) {
      return;
    }
    this._dialogReturnTarget = returnTarget ?? null;
    // Prefer a queue an existing stage already lives in — defaulting to
    // availableQueues[0] can pick a host-supplied queue the service blueprint itself
    // never uses, which silently creates a same-labelled duplicate lane
    // (the new gateway's queue key looks identical to an existing one in
    // the UI but isn't, since lanes group by key, not label).
    const defaultQueue =
      stageQueueKey(this.serviceBlueprint.stages[0]) ||
      serviceBlueprintQueueOptions(this.serviceBlueprint, this.availableQueues)[0] ||
      'public';
    this._createGatewayDialog = {
      title: '',
      gatewayKey: '',
      kind: 'Split',
      queueKey: defaultQueue,
      keyTouched: false,
      error: null,
    };
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLInputElement>('[data-wayfinder-create-gateway-title]')?.focus();
    });
  }

  private _closeCreateGatewayDialog() {
    this._createGatewayDialog = null;
    this._dialogReturnTarget?.focus();
    this._dialogReturnTarget = null;
  }

  private _submitCreateGateway() {
    if (!this.serviceBlueprint || !this._createGatewayDialog) {
      return;
    }

    const dialog = this._createGatewayDialog;
    const title = dialog.title.trim();
    const key = dialog.gatewayKey.trim();

    if (!title) {
      this._createGatewayDialog = { ...dialog, error: 'Gateway name is required.' };
      return;
    }

    if (!key) {
      this._createGatewayDialog = { ...dialog, error: 'Gateway key is required.' };
      return;
    }

    const usedKeys = [...this.serviceBlueprint.stages.map((s) => s.stageKey), ...(this.serviceBlueprint.gateways ?? []).map((g) => g.key)];
    if (usedKeys.includes(key)) {
      this._createGatewayDialog = { ...dialog, error: 'Gateway key must be unique across all stages and gateways.' };
      return;
    }

    const newGateway: ServiceBlueprintGatewayDefinition = {
      key,
      displayName: title,
      gatewayType: dialog.kind,
      queueKey: dialog.queueKey,
      actor: dialog.queueKey,
      roleGates: [],
    };

    const serviceBlueprint: ServiceBlueprint = {
      ...this.serviceBlueprint,
      gateways: [...serviceBlueprintGateways(this.serviceBlueprint), newGateway],
    };

    this._emitServiceBlueprintUpdated(serviceBlueprint, { kind: 'gateway', gatewayKey: newGateway.key });
    this._announce(`${title} ${dialog.kind} gateway created.`);
    this._closeCreateGatewayDialog();
    // Same as stage creation: an unconnected gateway has no anchor, so it
    // can land anywhere in its queue's rank-0 row — bring it into view.
    requestAnimationFrame(() => this._bridge?.centerOnNode(gatewayNodeId(newGateway.key)));
  }

  private _openDeleteStageDialog(stageKey: string, returnTarget?: HTMLElement | null) {
    if (!this.serviceBlueprint) {
      return;
    }

    this._dialogReturnTarget = returnTarget ?? this._contextReturnTarget ?? null;
    this._deleteStageDialog = {
      stageKey,
      affectedTransitions: flattenRoutes(this.serviceBlueprint).filter(
        (transition) => transition.fromStage === stageKey || transition.toStage === stageKey
      ),
    };
    this._dismissContextMenu(false);
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLButtonElement>('[data-wayfinder-delete-stage-cancel]')?.focus();
    });
  }

  private _closeDeleteStageDialog() {
    this._deleteStageDialog = null;
    const returnTarget = this._dialogReturnTarget;
    this._dialogReturnTarget = null;
    requestAnimationFrame(() => returnTarget?.focus());
  }

  private _confirmDeleteStage() {
    if (!this.serviceBlueprint || !this._deleteStageDialog) {
      return;
    }

    const stageKey = this._deleteStageDialog.stageKey;
    const deletedLabel = this._labelForStage(stageKey);
    const transitionCount = this._deleteStageDialog.affectedTransitions.length;
    const stages = this.serviceBlueprint.stages.filter((stage) => stage.stageKey !== stageKey);

    // Drop any gateway whose source was this stage, and remove any route
    // that targeted this stage. The derived `transitions` view is rebuilt
    // by `withDerivedTransitions` before we hand the service blueprint downstream.
    const gateways = serviceBlueprintGateways(this.serviceBlueprint)
      .filter((gateway) => gateway.key !== stageKey)
      .map((gateway) => ({
        ...gateway,
        routes: (gateway.routes ?? []).filter((route) => route.target !== stageKey),
      }));
    const stagesWithRoutes = stages.map((stage) => ({
      ...stage,
      routes: (stage.routes ?? []).filter((route) => route.target !== stageKey),
    }));

    const serviceBlueprint: ServiceBlueprint = pruneLayout({
      ...this.serviceBlueprint,
      stages: stagesWithRoutes,
      gateways,
      initialStage: this.serviceBlueprint.initialStage === stageKey ? (stages[0]?.stageKey ?? '') : this.serviceBlueprint.initialStage,
    });

    this._selectedStageKey = null;
    this._selectedTransitionIndex = null;
    this._emitServiceBlueprintUpdated(serviceBlueprint, null);
    this._announce(
      `${deletedLabel} deleted.${transitionCount > 0 ? ` ${transitionCount} affected transition${transitionCount === 1 ? '' : 's'} removed.` : ''}`
    );
    this._closeDeleteStageDialog();
  }

  private _openDeleteGatewayDialog(gatewayKey: string, returnTarget?: HTMLElement | null) {
    if (!this.serviceBlueprint) {
      return;
    }

    this._dialogReturnTarget = returnTarget ?? this._contextReturnTarget ?? null;
    this._deleteGatewayDialog = {
      gatewayKey,
      affectedTransitions: flattenRoutes(this.serviceBlueprint).filter(
        (transition) => transition.fromStage === gatewayKey || transition.toStage === gatewayKey
      ),
    };
    this._dismissContextMenu(false);
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLButtonElement>('[data-wayfinder-delete-gateway-cancel]')?.focus();
    });
  }

  private _closeDeleteGatewayDialog() {
    this._deleteGatewayDialog = null;
    const returnTarget = this._dialogReturnTarget;
    this._dialogReturnTarget = null;
    requestAnimationFrame(() => returnTarget?.focus());
  }

  private _confirmDeleteGateway() {
    if (!this.serviceBlueprint || !this._deleteGatewayDialog) {
      return;
    }

    const gatewayKey = this._deleteGatewayDialog.gatewayKey;
    const deletedLabel = this._labelForStage(gatewayKey);
    const transitionCount = this._deleteGatewayDialog.affectedTransitions.length;
    const gateways = serviceBlueprintGateways(this.serviceBlueprint).filter((gateway) => gateway.key !== gatewayKey);
    const gatewaysWithRoutes = gateways.map((gateway) => ({
      ...gateway,
      routes: (gateway.routes ?? []).filter((route) => route.target !== gatewayKey),
    }));
    const stagesWithRoutes = this.serviceBlueprint.stages.map((stage) => ({
      ...stage,
      routes: (stage.routes ?? []).filter((route) => route.target !== gatewayKey),
    }));

    const serviceBlueprint: ServiceBlueprint = pruneLayout({
      ...this.serviceBlueprint,
      stages: stagesWithRoutes,
      gateways: gatewaysWithRoutes,
    });

    this._selectedGatewayKey = null;
    this._selectedTransitionIndex = null;
    this._emitServiceBlueprintUpdated(serviceBlueprint, null);
    this._announce(
      `${deletedLabel} deleted.${transitionCount > 0 ? ` ${transitionCount} affected transition${transitionCount === 1 ? '' : 's'} removed.` : ''}`
    );
    this._closeDeleteGatewayDialog();
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
      this._openCreateStageDialog(
        referenceStage ? this._surfaceForStage(referenceStage) : 'front-stage',
        target.kind === 'stage' ? 'after' : 'append',
        target.kind === 'stage' ? target.stageKey : null
      );
      return;
    }

    if (target.kind === 'stage') {
      if (action === 'copy-stage') {
        void this._copyStage(target.stageKey);
      } else if (action === 'delete-stage') {
        this._openDeleteStageDialog(target.stageKey);
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
        this._openDeleteGatewayDialog(target.gatewayKey);
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

  private _handleDialogKeydown(event: KeyboardEvent, onClose: () => void) {
    if (event.key === 'Escape') {
      event.preventDefault();
      onClose();
      return;
    }

    if (event.key !== 'Tab') {
      return;
    }

    const root = event.currentTarget as HTMLElement;
    const focusable = Array.from(
      root.querySelectorAll<HTMLElement>('button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])')
    ).filter((element) => !element.hasAttribute('disabled') && element.tabIndex >= 0);
    if (focusable.length === 0) {
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const activeElement = this.shadowRoot?.activeElement as HTMLElement | null;
    if (event.shiftKey && activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  private _renderCreateStageDialog() {
    const dialog = this._createStageDialog;
    if (!dialog) {
      return nothing;
    }

    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel"
          role="dialog"
          aria-modal="true"
          aria-labelledby="create-stage-dialog-title"
          aria-describedby="create-stage-dialog-copy"
          data-wayfinder-create-stage-dialog
          @keydown=${(event: KeyboardEvent) => this._handleDialogKeydown(event, () => this._closeCreateStageDialog())}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow">Stage creation</p>
              <h2 id="create-stage-dialog-title" class="dialog-title">Create stage</h2>
            </div>
          </div>
          <p id="create-stage-dialog-copy" class="dialog-copy">
            Name the stage, choose its key, queue, and type, then continue editing in the inspector.
          </p>
          ${dialog.error ? html`<p class="dialog-error" data-wayfinder-create-stage-error>${dialog.error}</p>` : nothing}
          <div class="dialog-grid">
            <label class="dialog-field">
              <span class="dialog-label">Name</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-title
                .value=${dialog.title}
                @input=${(event: Event) => this._updateCreateStageTitle((event.currentTarget as HTMLInputElement).value)}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Key</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-key
                .value=${dialog.stageKey}
                @input=${(event: Event) => this._updateCreateStageKey((event.currentTarget as HTMLInputElement).value)}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Queue</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-queue
                .value=${dialog.queueKey}
                list="create-stage-queue-options"
                placeholder="planning"
                @input=${(event: Event) => this._updateCreateStageQueue((event.currentTarget as HTMLInputElement).value)}
              />
              <datalist id="create-stage-queue-options">
                ${this._availableQueueKeys().map(
                  (option) => html`
                  <option value=${option}>${this._roleLabelForQueue(option)}</option>
                `
                )}
              </datalist>
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Type</span>
              <select
                class="dialog-control"
                data-wayfinder-create-stage-type
                @change=${(event: Event) => {
                  const stageType = (event.currentTarget as HTMLSelectElement).value as StageKind;
                  this._createStageDialog = this._createStageDialog ? { ...this._createStageDialog, stageType } : null;
                }}
              >
                ${STAGE_KIND_OPTIONS.map(
                  (option) => html`
                  <option value=${option.value} ?selected=${dialog.stageType === option.value}>${option.label}</option>
                `
                )}
              </select>
            </label>
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" @click=${this._closeCreateStageDialog}>Cancel</button>
            <button type="button" class="dialog-button primary" data-wayfinder-create-stage-submit @click=${this._submitCreateStage}>Create stage</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderDeleteStageDialog() {
    const dialog = this._deleteStageDialog;
    if (!dialog) {
      return nothing;
    }

    const stageLabel = this._labelForStage(dialog.stageKey);
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel dialog-panel-danger"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-stage-dialog-title"
          aria-describedby="delete-stage-dialog-copy"
          data-wayfinder-delete-stage-dialog
          @keydown=${(event: KeyboardEvent) => this._handleDialogKeydown(event, () => this._closeDeleteStageDialog())}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow danger">Delete stage</p>
              <h2 id="delete-stage-dialog-title" class="dialog-title">Delete ${stageLabel}?</h2>
            </div>
          </div>
          <p id="delete-stage-dialog-copy" class="dialog-copy">
            This removes the stage and every transition connected to it.
          </p>
          <div class="delete-impact" data-wayfinder-delete-stage-transitions>
            ${
              dialog.affectedTransitions.length === 0
                ? html`<p>No transitions will be removed.</p>`
                : html`
                  <p>${dialog.affectedTransitions.length} affected transition${dialog.affectedTransitions.length === 1 ? '' : 's'}:</p>
                  <ul>
                    ${dialog.affectedTransitions.map(
                      (transition) => html`
                      <li>${this._labelForStage(transition.fromStage)} → ${this._labelForStage(transition.toStage)} (${transition.action})</li>
                    `
                    )}
                  </ul>
                `
            }
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" data-wayfinder-delete-stage-cancel @click=${this._closeDeleteStageDialog}>Cancel</button>
            <button type="button" class="dialog-button danger" data-wayfinder-delete-stage-confirm @click=${this._confirmDeleteStage}>Delete stage</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderDeleteGatewayDialog() {
    const dialog = this._deleteGatewayDialog;
    if (!dialog) {
      return nothing;
    }

    const gatewayLabel = this._labelForStage(dialog.gatewayKey);
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel dialog-panel-danger"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-gateway-dialog-title"
          aria-describedby="delete-gateway-dialog-copy"
          data-wayfinder-delete-gateway-dialog
          @keydown=${(event: KeyboardEvent) => this._handleDialogKeydown(event, () => this._closeDeleteGatewayDialog())}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow danger">Delete gateway</p>
              <h2 id="delete-gateway-dialog-title" class="dialog-title">Delete ${gatewayLabel}?</h2>
            </div>
          </div>
          <p id="delete-gateway-dialog-copy" class="dialog-copy">
            This removes the gateway and every transition connected to it.
          </p>
          <div class="delete-impact" data-wayfinder-delete-gateway-transitions>
            ${
              dialog.affectedTransitions.length === 0
                ? html`<p>No transitions will be removed.</p>`
                : html`
                  <p>${dialog.affectedTransitions.length} affected transition${dialog.affectedTransitions.length === 1 ? '' : 's'}:</p>
                  <ul>
                    ${dialog.affectedTransitions.map(
                      (transition) => html`
                      <li>${this._labelForStage(transition.fromStage)} → ${this._labelForStage(transition.toStage)} (${transition.action})</li>
                    `
                    )}
                  </ul>
                `
            }
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" data-wayfinder-delete-gateway-cancel @click=${this._closeDeleteGatewayDialog}>Cancel</button>
            <button type="button" class="dialog-button danger" data-wayfinder-delete-gateway-confirm @click=${this._confirmDeleteGateway}>Delete gateway</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderCreateGatewayDialog() {
    const dialog = this._createGatewayDialog;
    if (!dialog) {
      return nothing;
    }

    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel"
          role="dialog"
          aria-modal="true"
          aria-labelledby="create-gateway-dialog-title"
          aria-describedby="create-gateway-dialog-copy"
          data-wayfinder-create-gateway-dialog
          @keydown=${(event: KeyboardEvent) => this._handleDialogKeydown(event, () => this._closeCreateGatewayDialog())}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow">Gateway creation</p>
              <h2 id="create-gateway-dialog-title" class="dialog-title">Add gateway</h2>
            </div>
          </div>
          <p id="create-gateway-dialog-copy" class="dialog-copy">
            Add a Split or Join gateway to the workspace. Continue editing in the inspector after creation.
          </p>
          ${dialog.error ? html`<p class="dialog-error" data-wayfinder-create-gateway-error>${dialog.error}</p>` : nothing}
          <div class="dialog-grid">
            <label class="dialog-field">
              <span class="dialog-label">Name</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-title
                .value=${dialog.title}
                @input=${(event: Event) => {
                  const title = (event.currentTarget as HTMLInputElement).value;
                  const gatewayKey = dialog.keyTouched
                    ? dialog.gatewayKey
                    : title
                        .toLowerCase()
                        .replace(/\s+/g, '-')
                        .replace(/[^a-z0-9-]/g, '');
                  this._createGatewayDialog = this._createGatewayDialog
                    ? { ...this._createGatewayDialog, title, gatewayKey, error: null }
                    : null;
                }}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Key</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-key
                .value=${dialog.gatewayKey}
                @input=${(event: Event) => {
                  const gatewayKey = (event.currentTarget as HTMLInputElement).value;
                  this._createGatewayDialog = this._createGatewayDialog
                    ? { ...this._createGatewayDialog, gatewayKey, keyTouched: true, error: null }
                    : null;
                }}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Kind</span>
              <select
                class="dialog-control"
                data-wayfinder-create-gateway-kind
                @change=${(event: Event) => {
                  const kind = (event.currentTarget as HTMLSelectElement).value as 'Split' | 'Join';
                  this._createGatewayDialog = this._createGatewayDialog ? { ...this._createGatewayDialog, kind } : null;
                }}
              >
                <option value="Split" ?selected=${dialog.kind === 'Split'}>Split — branches into multiple paths</option>
                <option value="Join" ?selected=${dialog.kind === 'Join'}>Join — converges multiple paths</option>
              </select>
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Queue</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-queue
                .value=${dialog.queueKey}
                list="create-gateway-queue-options"
                placeholder="applicant"
                @input=${(event: Event) => {
                  const queueKey = (event.currentTarget as HTMLInputElement).value;
                  this._createGatewayDialog = this._createGatewayDialog ? { ...this._createGatewayDialog, queueKey } : null;
                }}
              />
              <datalist id="create-gateway-queue-options">
                ${this._availableQueueKeys().map(
                  (option) => html`
                  <option value=${option}>${this._roleLabelForQueue(option)}</option>
                `
                )}
              </datalist>
            </label>
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" @click=${this._closeCreateGatewayDialog}>Cancel</button>
            <button type="button" class="dialog-button primary" data-wayfinder-create-gateway-submit @click=${this._submitCreateGateway}>Create gateway</button>
          </div>
        </div>
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
                  @click=${(event: Event) => this._openCreateStageDialog('front-stage', 'append', null, event.currentTarget as HTMLElement)}
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
        ${this.readOnly ? nothing : this._renderCreateStageDialog()}
        ${this.readOnly ? nothing : this._renderDeleteStageDialog()}
        ${this.readOnly ? nothing : this._renderCreateGatewayDialog()}
        ${this.readOnly ? nothing : this._renderDeleteGatewayDialog()}
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
