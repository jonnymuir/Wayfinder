import type { RouteView, ServiceBlueprint, ServiceBlueprintGatewayDefinition, StageDefinition } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import { flattenRoutes } from '../route-model.js';
import { pruneLayout } from './service-blueprint-graph-layout-block.js';

/**
 * The structural edits an author can make from the graph: adding and removing stages and
 * gateways. Pure — each takes a blueprint and returns the next one — so the rules (unique keys,
 * where a new stage lands, what a delete cascades to) live in one place, separate from the
 * dialogs that collect the input.
 */

export type StagePlacement = { position: 'append' } | { position: 'before' | 'after'; referenceStageKey: string };

/** Every key a stage or gateway can be addressed by; they share one namespace. */
export function usedNodeKeys(blueprint: ServiceBlueprint): Set<string> {
  return new Set([
    ...blueprint.stages.map((stage) => stage.stageKey),
    ...serviceBlueprintGateways(blueprint).map((gateway) => gateway.key),
  ]);
}

/** A slug of `value`, suffixed `-2`, `-3`… until no stage already uses it. */
export function uniqueStageKey(blueprint: ServiceBlueprint | null, value: string, fallback: string): string {
  const base =
    value
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '') || fallback;
  const used = new Set(blueprint?.stages.map((stage) => stage.stageKey) ?? []);
  let candidate = base;
  for (let suffix = 2; used.has(candidate); suffix += 1) {
    candidate = `${base}-${suffix}`;
  }
  return candidate;
}

export function slugifyGatewayKey(title: string): string {
  return title
    .toLowerCase()
    .replace(/\s+/g, '-')
    .replace(/[^a-z0-9-]/g, '');
}

export function insertStage(blueprint: ServiceBlueprint, stage: StageDefinition, placement: StagePlacement): ServiceBlueprint {
  const stages = [...blueprint.stages];
  let insertIndex = stages.length;
  if (placement.position !== 'append') {
    const referenceIndex = stages.findIndex((candidate) => candidate.stageKey === placement.referenceStageKey);
    if (referenceIndex >= 0) {
      insertIndex = placement.position === 'before' ? referenceIndex : referenceIndex + 1;
    }
  }
  stages.splice(insertIndex, 0, stage);
  return { ...blueprint, initialStage: blueprint.initialStage || stage.stageKey, stages };
}

export function insertGateway(blueprint: ServiceBlueprint, gateway: ServiceBlueprintGatewayDefinition): ServiceBlueprint {
  return { ...blueprint, gateways: [...serviceBlueprintGateways(blueprint), gateway] };
}

/** The routes a delete of `nodeKey` would take with it: those leaving or arriving at it. */
export function routesTouching(blueprint: ServiceBlueprint, nodeKey: string): RouteView[] {
  return flattenRoutes(blueprint).filter((route) => route.fromStage === nodeKey || route.toStage === nodeKey);
}

/**
 * Removes a stage or gateway, every route that targeted it, and its saved layout. If it was the
 * initial stage, the first remaining stage takes over.
 */
export function removeNode(blueprint: ServiceBlueprint, nodeKey: string): ServiceBlueprint {
  const stages = blueprint.stages.filter((stage) => stage.stageKey !== nodeKey);
  const withoutRoutesTo = <T extends { routes?: { target: string }[] }>(node: T): T => ({
    ...node,
    routes: (node.routes ?? []).filter((route) => route.target !== nodeKey),
  });

  return pruneLayout({
    ...blueprint,
    stages: stages.map(withoutRoutesTo),
    gateways: serviceBlueprintGateways(blueprint)
      .filter((gateway) => gateway.key !== nodeKey)
      .map(withoutRoutesTo),
    initialStage: blueprint.initialStage === nodeKey ? (stages[0]?.stageKey ?? '') : blueprint.initialStage,
  });
}
