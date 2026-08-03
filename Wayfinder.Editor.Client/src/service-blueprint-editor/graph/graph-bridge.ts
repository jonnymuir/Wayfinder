import { createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type { ReactFlowInstance } from '@xyflow/react';
import { GraphApp } from './graph-app.js';
import type { GraphCallbacks, GraphProps } from './graph-callbacks.js';
import type { GraphFlowNode, RouteFlowEdge } from './graph-model.js';

type FlowInstance = ReactFlowInstance<GraphFlowNode, RouteFlowEdge>;

function snapshotsEqual(left: GraphProps, right: GraphProps): boolean {
  return (Object.keys(left) as Array<keyof GraphProps>)
    .every(key => Object.is(left[key], right[key]));
}

/**
 * Owns the React root for the graph canvas inside the Lit component's shadow
 * root. Renders once; subsequent Lit updates flow through a
 * useSyncExternalStore snapshot instead of repeated root.render calls.
 */
export class GraphBridge {
  readonly callbacks: GraphCallbacks;

  private root: Root | null;
  private snapshot: GraphProps;
  private readonly listeners = new Set<() => void>();
  private flow: FlowInstance | null = null;
  private bounds: { width: number; height: number } | null = null;

  constructor(container: HTMLElement, initialProps: GraphProps, callbacks: GraphCallbacks) {
    this.callbacks = callbacks;
    this.snapshot = initialProps;
    this.root = createRoot(container);
    this.root.render(createElement(GraphApp, { bridge: this }));
  }

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getSnapshot = (): GraphProps => this.snapshot;

  update(props: GraphProps) {
    if (snapshotsEqual(this.snapshot, props)) {
      return;
    }
    this.snapshot = props;
    this.listeners.forEach(listener => listener());
  }

  setFlowInstance(flow: FlowInstance) {
    this.flow = flow;
  }

  /**
   * The diagram's real content extent (service-blueprint-graph-layout.ts's computeLayout,
   * relayed from graph-app.tsx) — an implicit (0, 0)-origin box that, unlike
   * flow.getNodesBounds(getNodes()), also accounts for LaneLayer's lane headers. Those render
   * as a plain sibling of the React Flow nodes, not as nodes themselves, so a node-only bounds
   * box doesn't reserve room for them — fitting against it alone renders the top lane header
   * (and part of the topmost node under it) above .graph-canvas's own clipped top edge:
   * invisible and unclickable, confirmed live. Used in preference to node-only bounds wherever
   * available; falls back to node-only bounds only for the brief window before the first
   * report arrives.
   */
  setBounds(bounds: { width: number; height: number }) {
    this.bounds = bounds;
  }

  fitView(): Promise<boolean> {
    return this.fitBounds({ padding: 0.1, duration: 200 });
  }

  /**
   * The initial-load fit — same as fitView, but capped at 100% zoom. A diagram that already
   * fits comfortably at 100% shouldn't be zoomed in just to fill the container to the fitView
   * padding target; only a diagram too big to fit should actually change zoom on load. Kept
   * separate from fitView (the "Fit" HUD button) since that button's own established behaviour
   * — zoom in for a small diagram to use more of the screen — is a deliberate user action, not
   * an unrequested default the moment the canvas opens.
   */
  fitViewOnLoad(): Promise<boolean> {
    return this.fitBounds({ padding: 0.1, duration: 0, maxZoom: 1 });
  }

  private fitBounds(options: { padding: number; duration: number; maxZoom?: number }): Promise<boolean> {
    if (!this.flow) {
      return Promise.resolve(false);
    }
    const bounds = this.currentBounds();
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) {
      return this.flow.fitView(options);
    }
    return this.flow.fitBounds({ x: 0, y: 0, width: bounds.width, height: bounds.height }, options);
  }

  private currentBounds(): { x: number; y: number; width: number; height: number } | null {
    if (this.bounds) {
      return { x: 0, y: 0, width: this.bounds.width, height: this.bounds.height };
    }
    if (!this.flow) {
      return null;
    }
    const nodes = this.flow.getNodes();
    return nodes.length > 0 ? this.flow.getNodesBounds(nodes) : null;
  }

  /** Pan/zoom so a single node (e.g. one just created) is visibly in frame. */
  centerOnNode(nodeId: string) {
    void this.flow?.fitView({ nodes: [{ id: nodeId }], padding: 0.6, duration: 260, maxZoom: 1 });
  }

  zoomIn() {
    void this.flow?.zoomIn({ duration: 120 });
  }

  zoomOut() {
    void this.flow?.zoomOut({ duration: 120 });
  }

  unmount() {
    const root = this.root;
    this.root = null;
    this.flow = null;
    // Deferred: React must never unmount synchronously from inside a Lit
    // lifecycle callback that may itself be running during a React render.
    queueMicrotask(() => root?.unmount());
  }
}
