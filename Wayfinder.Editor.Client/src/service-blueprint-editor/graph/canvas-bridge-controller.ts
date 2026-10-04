import type { ReactiveController, ReactiveControllerHost } from 'lit';
import type { GraphBridge } from './graph-bridge.js';
import type { GraphCallbacks, GraphProps } from './graph-callbacks.js';

export interface CanvasBridgeContext {
  /** The shadow root the canvas mounts into, and whose host gets the ready attribute. */
  root(): ShadowRoot | null;
  readyHost(): HTMLElement;
  isConnected(): boolean;
  props(): GraphProps;
  callbacks(): GraphCallbacks;
}

/** Hosts may recreate array props on every render; reuse the previous reference when the contents are unchanged. */
function stableArray<T>(next: T[], prior: T[] | undefined): T[] {
  return prior && prior.length === next.length && next.every((value, index) => value === prior[index]) ? prior : next;
}

/**
 * The React Flow canvas the graph element draws with. Mounts it into the `.graph-react-host` the
 * template renders, pushes each update's props into it, and unmounts it when the host element goes.
 * The React bundle (react, react-dom, @xyflow/react) loads lazily on first mount so definition-only
 * usage never downloads it — mirroring how CodeMirror is deferred.
 */
export class CanvasBridgeController implements ReactiveController {
  private _bridge: GraphBridge | null = null;
  private _bridgeHost: HTMLElement | null = null;
  private _loading = false;
  private _lastSnapshot: GraphProps | null = null;

  constructor(
    host: ReactiveControllerHost,
    private readonly _context: CanvasBridgeContext
  ) {
    host.addController(this);
  }

  hostUpdated(): void {
    this._sync();
  }

  hostDisconnected(): void {
    this._teardown();
  }

  fitView() {
    return this._bridge?.fitView();
  }

  fitWidth() {
    return this._bridge?.fitWidth();
  }

  zoomIn() {
    this._bridge?.zoomIn();
  }

  zoomOut() {
    this._bridge?.zoomOut();
  }

  centerOnNode(nodeId: string) {
    this._bridge?.centerOnNode(nodeId);
  }

  /** Reuses unchanged references so the canvas only re-renders — and re-seeds its local node state — on genuine changes. */
  private _snapshot(): GraphProps {
    const previous = this._lastSnapshot;
    const props = this._context.props();
    this._lastSnapshot = {
      ...props,
      availableQueues: stableArray(props.availableQueues, previous?.availableQueues),
      simulationPathStageKeys: stableArray(props.simulationPathStageKeys, previous?.simulationPathStageKeys),
      simulationPathTransitionIndices: stableArray(props.simulationPathTransitionIndices, previous?.simulationPathTransitionIndices),
    };
    return this._lastSnapshot;
  }

  private _sync() {
    const host = this._context.root()?.querySelector<HTMLElement>('.graph-react-host') ?? null;
    if (!host) {
      this._teardown();
      return;
    }

    if (this._bridge && this._bridgeHost === host) {
      this._bridge.update(this._snapshot());
      return;
    }

    if (this._bridge) {
      this._teardown();
    }
    if (!this._loading) {
      void this._mount();
    }
  }

  private async _mount() {
    this._loading = true;
    try {
      const [{ GraphBridge: GraphBridgeCtor }, { graphStyleSheets }] = await Promise.all([
        import('./graph-bridge.js'),
        import('./graph-styles.js'),
      ]);
      const root = this._context.root();
      if (!this._context.isConnected() || !root) {
        return;
      }

      for (const sheet of graphStyleSheets()) {
        if (!root.adoptedStyleSheets.includes(sheet)) {
          root.adoptedStyleSheets = [...root.adoptedStyleSheets, sheet];
        }
      }

      const currentHost = root.querySelector<HTMLElement>('.graph-react-host');
      if (currentHost) {
        this._bridgeHost = currentHost;
        this._bridge = new GraphBridgeCtor(currentHost, this._snapshot(), this._context.callbacks());
      }
    } finally {
      this._loading = false;
    }
  }

  private _teardown() {
    this._bridge?.unmount();
    this._bridge = null;
    this._bridgeHost = null;
    this._context.readyHost().removeAttribute('data-wayfinder-graph-ready');
  }
}
