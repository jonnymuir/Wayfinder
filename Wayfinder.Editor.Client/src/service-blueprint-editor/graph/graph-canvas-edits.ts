import type { ServiceBlueprint } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import { applyQueueToStage } from '../stage-assignment.js';
import { addRoute, buildRoute, findOrCreateSplitGateway, flattenRoutes } from '../route-model.js';
import type { GraphNodeMove } from './graph-callbacks.js';
import { parseGraphNodeId } from './service-blueprint-graph-layout.js';
import { setNodePositions } from './service-blueprint-graph-layout-block.js';

/**
 * What the author's gestures on the canvas do to the blueprint: dragging nodes (and dropping them
 * into another queue's lane) and dragging a connection from one node to another. Pure — each takes a
 * blueprint and returns the next — so the rules live apart from the element that wires the gestures.
 */

/** Moves nodes to their dropped positions, and reassigns any that landed in a different lane to that lane's queue. */
export function applyNodeMoves(blueprint: ServiceBlueprint, moves: GraphNodeMove[]): ServiceBlueprint {
  const positioned = setNodePositions(blueprint, Object.fromEntries(moves.map((move) => [move.nodeId, { x: move.x, y: move.y }])));
  return moves.reduce((next, move) => (move.queueKey ? assignQueue(next, move.nodeId, move.queueKey) : next), positioned);
}

function assignQueue(blueprint: ServiceBlueprint, nodeId: string, queueKey: string): ServiceBlueprint {
  const node = parseGraphNodeId(nodeId);
  if (node.kind === 'stage') {
    return {
      ...blueprint,
      stages: blueprint.stages.map((stage) => (stage.stageKey === node.key ? applyQueueToStage(stage, queueKey) : stage)),
    };
  }
  return {
    ...blueprint,
    gateways: serviceBlueprintGateways(blueprint).map((gateway) =>
      gateway.key === node.key ? { ...gateway, queueKey, actor: queueKey } : gateway
    ),
  };
}

export type Connection = { blueprint: ServiceBlueprint; routeId: string };

/**
 * Drag-to-connect. The gateway-routing invariant is preserved by construction: stage→stage
 * connections are routed through the source's Split gateway (created on demand); stage routes may
 * target gateways directly; gateway routes may target anything. Returns `'duplicate'` if that exact
 * "continue" route already exists, and null for a connection from a node to itself.
 */
export function connectNodes(blueprint: ServiceBlueprint, sourceId: string, targetId: string): Connection | 'duplicate' | null {
  const source = parseGraphNodeId(sourceId);
  const target = parseGraphNodeId(targetId);
  if (source.key === target.key) {
    return null;
  }

  const viaSplitGateway = source.kind === 'stage' && target.kind === 'stage';
  const ensured = viaSplitGateway
    ? findOrCreateSplitGateway(blueprint, source.key)
    : { serviceBlueprint: blueprint, gatewayKey: source.key };
  const route = buildRoute({ source: ensured.gatewayKey, target: target.key, trigger: 'continue' });
  if (flattenRoutes(ensured.serviceBlueprint).some((view) => view.routeId === route.id)) {
    return 'duplicate';
  }

  const ownedByStage = !viaSplitGateway && source.kind === 'stage';
  const next = ownedByStage
    ? {
        ...ensured.serviceBlueprint,
        stages: ensured.serviceBlueprint.stages.map((stage) =>
          stage.stageKey === source.key ? { ...stage, routes: [...(stage.routes ?? []), route] } : stage
        ),
      }
    : addRoute(ensured.serviceBlueprint, ensured.gatewayKey, route);
  return { blueprint: next, routeId: route.id };
}

/** The sentence announced after a set of nodes was moved. */
export function describeNodeMoves(
  moves: GraphNodeMove[],
  labelOf: (nodeKey: string) => string,
  queueLabelOf: (queueKey: string) => string
): string {
  const reassigned = moves.find((move) => move.queueKey);
  if (reassigned) {
    return `${labelOf(parseGraphNodeId(reassigned.nodeId).key)} moved to the ${queueLabelOf(reassigned.queueKey!)} queue.`;
  }
  return moves.length === 1 ? `${labelOf(parseGraphNodeId(moves[0].nodeId).key)} moved.` : `${moves.length} nodes moved.`;
}
