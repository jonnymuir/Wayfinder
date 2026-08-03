import { createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type { ReactFlowInstance } from '@xyflow/react';
import { GraphApp } from './graph-app.js';
import type { GraphCallbacks, GraphProps } from './graph-callbacks.js';
import type { GraphFlowNode, RouteFlowEdge } from './graph-model.js';
import { NODE_WIDTH } from './service-blueprint-graph-layout.js';

/** Never shrink a stage below this on-screen width on the initial view — smaller and its
 * label/icon stop being legible at a glance. */
const MIN_LEGIBLE_STAGE_SCREEN_WIDTH = 200;

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
  private readonly container: HTMLElement;

  constructor(container: HTMLElement, initialProps: GraphProps, callbacks: GraphCallbacks) {
    this.callbacks = callbacks;
    this.snapshot = initialProps;
    this.container = container;
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
   * Zoom/pan so the diagram's full width fits the canvas — unlike fitView (which fits both
   * axes and so is bottlenecked by whichever is more constrained), this always shows every
   * lane edge-to-edge, at the cost of possibly needing to pan vertically to see a tall
   * diagram's top/bottom. Top-anchored (not centred) — the natural place to start reading a
   * service blueprint is its first row, not whatever happens to be in the middle.
   */
  fitWidth(): Promise<boolean> {
    return this.fitWidthAtZoom({ floorZoom: 0.1, ceilingZoom: 2 });
  }

  /**
   * The initial-load fit: width, but never zoomed in past what's needed to keep a stage
   * legible (see MIN_LEGIBLE_STAGE_SCREEN_WIDTH) — a wide diagram would otherwise shrink
   * every stage to an unreadable sliver just to avoid any horizontal pan. Whichever zoom is
   * larger (fit-width's, or the legibility floor's) wins; the trade-off is that a genuinely
   * wide diagram needs horizontal pan/scroll to see the rest, same as fitWidth always risked.
   * Capped at 100%, same reasoning as fitView's own on-load cap — a diagram narrower than the
   * container (e.g. a single lane) shouldn't be blown up just to fill the available width;
   * only a diagram too wide to fit should actually change zoom on load. Not fitView (fits both
   * axes) — a plain "show everything at once" view was tried first and reported as less
   * pleasant to land on than fitting the width and letting height overflow, which is also how
   * a document/page naturally reads.
   */
  fitViewOnLoad(): Promise<boolean> {
    const bounds = this.currentBounds();
    if (!bounds || bounds.width <= 0) {
      return this.fitBounds({ padding: 0.1, duration: 0, maxZoom: 1 });
    }
    const minLegibleZoom = MIN_LEGIBLE_STAGE_SCREEN_WIDTH / NODE_WIDTH;
    return this.fitWidthAtZoom({ floorZoom: minLegibleZoom, ceilingZoom: 1 });
  }

  private fitWidthAtZoom(limits: { floorZoom: number; ceilingZoom: number }): Promise<boolean> {
    if (!this.flow) {
      return Promise.resolve(false);
    }
    const bounds = this.currentBounds();
    if (!bounds || bounds.width <= 0) {
      return this.flow.fitView({ padding: 0.1, duration: 0 });
    }
    const containerRect = this.container.getBoundingClientRect();
    const padding = 40;
    const widthFitZoom = (containerRect.width - padding * 2) / bounds.width;
    const zoom = Math.min(limits.ceilingZoom, Math.max(limits.floorZoom, widthFitZoom));
    const x = -bounds.x * zoom + padding;
    const y = padding - bounds.y * zoom;
    return this.flow.setViewport({ x, y, zoom }, { duration: 0 });
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
