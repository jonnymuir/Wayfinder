import type { AuthoredServiceBlueprint, ServiceBlueprintNodePosition } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import type { QueueDefinition } from '../stage-assignment.js';
import {
  computeDerivedLayout,
  computeTopology,
  gatewayNodeId,
  stageNodeId,
} from './service-blueprint-graph-layout.js';

/**
 * Immutable helpers for the definition's `layout` block. Positions are stored
 * in whole flow pixels keyed by prefixed node id; queue membership stays on
 * the stages/gateways themselves.
 */

function roundPosition(position: ServiceBlueprintNodePosition): ServiceBlueprintNodePosition {
  return { x: Math.round(position.x), y: Math.round(position.y) };
}

export function getNodePosition(
  serviceBlueprint: AuthoredServiceBlueprint,
  nodeId: string
): ServiceBlueprintNodePosition | null {
  return serviceBlueprint.layout?.nodes?.[nodeId] ?? null;
}

/** Returns a new serviceBlueprint with the given node positions written into the layout block. */
export function setNodePositions(
  serviceBlueprint: AuthoredServiceBlueprint,
  positions: Record<string, ServiceBlueprintNodePosition>
): AuthoredServiceBlueprint {
  const nodes: Record<string, ServiceBlueprintNodePosition> = { ...(serviceBlueprint.layout?.nodes ?? {}) };
  for (const [nodeId, position] of Object.entries(positions)) {
    nodes[nodeId] = roundPosition(position);
  }
  return pruneLayout({ ...serviceBlueprint, layout: { ...serviceBlueprint.layout, nodes } });
}

/**
 * Returns a new serviceBlueprint with a route's manual bend point set (or,
 * passing `position: null`, cleared back to the derived auto-routed path).
 * Keyed by the same graph edge key ("fromId->toId") RouteEdge renders with.
 */
export function setRouteWaypoint(
  serviceBlueprint: AuthoredServiceBlueprint,
  edgeKey: string,
  position: ServiceBlueprintNodePosition | null
): AuthoredServiceBlueprint {
  const routes: Record<string, ServiceBlueprintNodePosition> = { ...(serviceBlueprint.layout?.routes ?? {}) };
  if (position) {
    routes[edgeKey] = roundPosition(position);
  } else {
    delete routes[edgeKey];
  }
  return pruneLayout({ ...serviceBlueprint, layout: { ...serviceBlueprint.layout, routes } });
}

/** Drops layout entries whose stage/gateway, or route endpoint, no longer exists. */
export function pruneLayout(serviceBlueprint: AuthoredServiceBlueprint): AuthoredServiceBlueprint {
  const nodeEntries = Object.entries(serviceBlueprint.layout?.nodes ?? {});
  const routeEntries = Object.entries(serviceBlueprint.layout?.routes ?? {});
  if (nodeEntries.length === 0 && routeEntries.length === 0) {
    return serviceBlueprint.layout === undefined ? serviceBlueprint : { ...serviceBlueprint, layout: undefined };
  }

  const liveIds = new Set<string>([
    ...serviceBlueprint.stages.map(stage => stageNodeId(stage.stateKey)),
    ...serviceBlueprintGateways(serviceBlueprint).map(gateway => gatewayNodeId(gateway.key)),
  ]);
  const nodes: Record<string, ServiceBlueprintNodePosition> = {};
  for (const [nodeId, position] of nodeEntries) {
    if (liveIds.has(nodeId)) {
      nodes[nodeId] = position;
    }
  }
  const routes: Record<string, ServiceBlueprintNodePosition> = {};
  for (const [edgeKey, position] of routeEntries) {
    const [fromId, toIdWithSuffix] = edgeKey.split('->');
    // Edges shared by more than one transition to the same target (e.g. approve/reject) get a
    // "#<transitionIndex>" suffix on toId to keep their keys distinct — strip it before checking
    // node liveness, or every suffixed key fails this check and its waypoint gets dropped right
    // after being set (toId would be checked as e.g. "gateway:foo#3", which never matches a real
    // node id).
    const toId = toIdWithSuffix?.split('#')[0];
    if (liveIds.has(fromId) && liveIds.has(toId)) {
      routes[edgeKey] = position;
    }
  }

  const hasNodes = Object.keys(nodes).length > 0;
  const hasRoutes = Object.keys(routes).length > 0;
  if (!hasNodes && !hasRoutes) {
    return { ...serviceBlueprint, layout: undefined };
  }
  return {
    ...serviceBlueprint,
    layout: {
      ...(hasNodes ? { nodes } : {}),
      ...(hasRoutes ? { routes } : {}),
    },
  };
}

/**
 * Tidy layout: recompute the derived auto-layout for every node (ignoring any
 * stored positions) and write the result back as explicit positions, so the
 * arrangement is deterministic and each node stays individually adjustable.
 */
export function applyAutoArrange(
  serviceBlueprint: AuthoredServiceBlueprint,
  availableQueues: QueueDefinition[] = []
): AuthoredServiceBlueprint {
  const layout = computeDerivedLayout(computeTopology(serviceBlueprint, availableQueues));
  const nodes: Record<string, ServiceBlueprintNodePosition> = {};
  layout.placements.forEach(placement => {
    nodes[placement.id] = roundPosition({ x: placement.x, y: placement.y });
  });
  if (Object.keys(nodes).length === 0) {
    return { ...serviceBlueprint, layout: undefined };
  }
  return { ...serviceBlueprint, layout: { nodes } };
}
