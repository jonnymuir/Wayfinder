import { flattenRoutes } from './route-model.js';
import type { ServiceBlueprint } from './types.js';
import { serviceBlueprintGateways, serviceBlueprintStages } from './types.js';

/** A stage or gateway, named so the author can recognise it. */
export interface NodeRef {
  key: string;
  kind: 'stage' | 'gateway';
  displayName: string;
}

/**
 * What changed between two versions of a blueprint, by identity: stages and gateways by key, routes by route id. A
 * stage or gateway counts as changed when anything about it other than its routes changed, since route changes are
 * reported (and highlighted) on the routes themselves.
 */
export interface BlueprintChanges {
  added: NodeRef[];
  changed: NodeRef[];
  removed: NodeRef[];
  /** Indices into the newer blueprint's flattened routes, the indices the canvas draws routes by. */
  addedRouteIndices: number[];
  changedRouteIndices: number[];
  removedRouteCount: number;
  /** Changes with nothing on the canvas to highlight, such as "calculations" or "queues". */
  otherChanges: string[];
}

type Node = NodeRef & { body: string };

/** Everything except the parts that move with the canvas or are reported elsewhere. */
function nodeBody(node: object): string {
  const { routes: _routes, ...rest } = node as { routes?: unknown };
  return JSON.stringify(rest);
}

function nodes(blueprint: ServiceBlueprint): Node[] {
  return [
    ...serviceBlueprintStages(blueprint).map((stage) => ({
      key: stage.stageKey,
      kind: 'stage' as const,
      displayName: stage.displayName,
      body: nodeBody(stage),
    })),
    ...serviceBlueprintGateways(blueprint).map((gateway) => ({
      key: gateway.key,
      kind: 'gateway' as const,
      displayName: gateway.displayName,
      body: nodeBody(gateway),
    })),
  ];
}

function routeBodies(blueprint: ServiceBlueprint): Map<string, { index: number; body: string }> {
  const owners = [
    ...serviceBlueprintStages(blueprint).flatMap((stage) => stage.routes ?? []),
    ...serviceBlueprintGateways(blueprint).flatMap((gateway) => gateway.routes ?? []),
  ];
  return new Map(
    flattenRoutes(blueprint).map((view) => [view.routeId, { index: view.routeIndex, body: JSON.stringify(owners[view.routeIndex]) }])
  );
}

const OTHER_PARTS: Array<[keyof ServiceBlueprint, string]> = [
  ['displayName', 'name'],
  ['description', 'description'],
  ['initialStage', 'first stage'],
  ['requestPolicy', 'request policy'],
  ['allowManualRestart', 'restart setting'],
  ['queues', 'queues'],
  ['calculations', 'calculations'],
  ['handoffs', 'handoffs'],
  ['tags', 'tags'],
];

export function diffBlueprints(before: ServiceBlueprint, after: ServiceBlueprint): BlueprintChanges {
  const beforeNodes = new Map(nodes(before).map((node) => [node.key, node]));
  const afterNodes = nodes(after);
  const afterKeys = new Set(afterNodes.map((node) => node.key));
  const ref = ({ key, kind, displayName }: Node): NodeRef => ({ key, kind, displayName });

  const beforeRoutes = routeBodies(before);
  const afterRoutes = routeBodies(after);

  return {
    added: afterNodes.filter((node) => !beforeNodes.has(node.key)).map(ref),
    changed: afterNodes.filter((node) => beforeNodes.has(node.key) && beforeNodes.get(node.key)?.body !== node.body).map(ref),
    removed: [...beforeNodes.values()].filter((node) => !afterKeys.has(node.key)).map(ref),
    addedRouteIndices: [...afterRoutes].filter(([id]) => !beforeRoutes.has(id)).map(([, route]) => route.index),
    changedRouteIndices: [...afterRoutes]
      .filter(([id, route]) => beforeRoutes.has(id) && beforeRoutes.get(id)?.body !== route.body)
      .map(([, route]) => route.index),
    removedRouteCount: [...beforeRoutes.keys()].filter((id) => !afterRoutes.has(id)).length,
    otherChanges: OTHER_PARTS.filter(([part]) => JSON.stringify(before[part]) !== JSON.stringify(after[part])).map(([, label]) => label),
  };
}

export function hasChanges(changes: BlueprintChanges): boolean {
  return (
    changes.added.length + changes.changed.length + changes.removed.length > 0 ||
    changes.addedRouteIndices.length + changes.changedRouteIndices.length + changes.removedRouteCount > 0 ||
    changes.otherChanges.length > 0
  );
}

function count(n: number, noun: string, verb: string): string | null {
  return n > 0 ? `${n} ${noun}${n === 1 ? '' : 's'} ${verb}` : null;
}

function countNodes(refs: NodeRef[], verb: string): Array<string | null> {
  return [
    count(refs.filter((node) => node.kind === 'stage').length, 'stage', verb),
    count(refs.filter((node) => node.kind === 'gateway').length, 'gateway', verb),
  ];
}

/** One line for the notice and screen readers, such as "2 stages added, 1 route changed, calculations changed". */
export function summariseChanges(changes: BlueprintChanges): string {
  const parts = [
    ...countNodes(changes.added, 'added'),
    ...countNodes(changes.changed, 'changed'),
    ...countNodes(changes.removed, 'removed'),
    count(changes.addedRouteIndices.length, 'route', 'added'),
    count(changes.changedRouteIndices.length, 'route', 'changed'),
    count(changes.removedRouteCount, 'route', 'removed'),
    ...changes.otherChanges.map((label) => `${label} changed`),
  ].filter((part): part is string => part !== null);
  return parts.length > 0 ? parts.join(', ') : 'only the layout changed';
}
