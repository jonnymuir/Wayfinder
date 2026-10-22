import type { EditorSelection } from './editor-selection.js';
import { findRoute, flattenRoutes } from './route-model.js';
import type { DesignNoteAnchor, ServiceBlueprint } from './types.js';
import { serviceBlueprintGateways, serviceBlueprintStages } from './types.js';

/** Where a note's anchor is now: a label for people, and the canvas node it belongs to (`stage:key` or `gateway:key`). */
export interface AnchorPlace {
  label: string;
  /** The thing it was pinned to has gone, so the note stands at the nearest level that still exists. */
  removed: boolean;
  nodeId: string | null;
}

/** Where the canvas goes to show a note's anchor. */
export interface AnchorTarget {
  selection: EditorSelection;
  transitionIndex: number | null;
}

const WHOLE_BLUEPRINT: AnchorPlace = { label: 'Whole blueprint', removed: false, nodeId: null };

const removed = (what: string): AnchorPlace => ({ label: `Whole blueprint (${what} was removed)`, removed: true, nodeId: null });

function stagePlace(blueprint: ServiceBlueprint, key: string | undefined): AnchorPlace {
  const stage = serviceBlueprintStages(blueprint).find((candidate) => candidate.stageKey === key);
  return stage ? { label: `Stage: ${stage.displayName}`, removed: false, nodeId: `stage:${stage.stageKey}` } : removed(`stage “${key}”`);
}

function gatewayPlace(blueprint: ServiceBlueprint, key: string | undefined): AnchorPlace {
  const gateway = serviceBlueprintGateways(blueprint).find((candidate) => candidate.key === key);
  return gateway
    ? { label: `Gateway: ${gateway.displayName}`, removed: false, nodeId: `gateway:${gateway.key}` }
    : removed(`gateway “${key}”`);
}

function routeOwner(blueprint: ServiceBlueprint, routeId: string | undefined): { nodeId: string; name: string; index: number } | null {
  const view = flattenRoutes(blueprint).find((candidate) => candidate.routeId === routeId);
  if (!view) {
    return null;
  }
  const owner = view.fromGateway ? gatewayPlace(blueprint, view.fromGateway) : stagePlace(blueprint, view.fromStage);
  return owner.nodeId ? { nodeId: owner.nodeId, name: owner.label.replace(/^\w+: /, ''), index: view.routeIndex } : null;
}

function routePlace(blueprint: ServiceBlueprint, routeId: string | undefined): AnchorPlace {
  const owner = routeOwner(blueprint, routeId);
  const trigger = owner ? flattenRoutes(blueprint)[owner.index].action : '';
  return owner ? { label: `Route: ${trigger}, from ${owner.name}`, removed: false, nodeId: owner.nodeId } : removed(`route “${routeId}”`);
}

/** Finds a field anywhere in a stage's component tree, whatever holds it (children, sections, items). */
function findField(value: unknown, fieldKey: string | undefined): { label?: unknown } | null {
  if (!value || typeof value !== 'object') {
    return null;
  }
  if (!Array.isArray(value) && (value as { fieldKey?: unknown }).fieldKey === fieldKey) {
    return value as { label?: unknown };
  }
  for (const child of Object.values(value)) {
    const found = findField(child, fieldKey);
    if (found) {
      return found;
    }
  }
  return null;
}

function fieldPlace(blueprint: ServiceBlueprint, stageKey: string | undefined, fieldKey: string | undefined): AnchorPlace {
  const stage = stagePlace(blueprint, stageKey);
  if (stage.removed) {
    return stage;
  }
  const field = findField(serviceBlueprintStages(blueprint).find((candidate) => candidate.stageKey === stageKey)?.components, fieldKey);
  if (!field) {
    return { ...stage, label: `${stage.label} (field “${fieldKey}” was removed)`, removed: true };
  }
  const name = typeof field.label === 'string' && field.label ? field.label : fieldKey;
  return { ...stage, label: `Field: ${name}, on ${stage.label.replace(/^Stage: /, '')}` };
}

export function placeAnchor(blueprint: ServiceBlueprint, anchor: DesignNoteAnchor): AnchorPlace {
  switch (anchor.kind) {
    case 'Stage':
      return stagePlace(blueprint, anchor.key);
    case 'Gateway':
      return gatewayPlace(blueprint, anchor.key);
    case 'Route':
      return routePlace(blueprint, anchor.key);
    case 'Field':
      return fieldPlace(blueprint, anchor.key, anchor.fieldKey);
    default:
      return WHOLE_BLUEPRINT;
  }
}

/** What a new note made with this selection is pinned to: the highlighted route if it has an id, else the stage or gateway, else the blueprint. */
export function anchorForSelection(
  blueprint: ServiceBlueprint,
  selection: EditorSelection,
  transitionIndex: number | null
): DesignNoteAnchor {
  const view = transitionIndex === null ? undefined : flattenRoutes(blueprint)[transitionIndex];
  if (view && findRoute(blueprint, view.routeId)?.route.id) {
    return { kind: 'Route', key: view.routeId };
  }
  if (selection?.kind === 'stage') {
    return { kind: 'Stage', key: selection.stageKey };
  }
  return selection?.kind === 'gateway' ? { kind: 'Gateway', key: selection.gatewayKey } : { kind: 'Blueprint' };
}

export function targetForAnchor(blueprint: ServiceBlueprint, anchor: DesignNoteAnchor): AnchorTarget | null {
  const nodeId = placeAnchor(blueprint, anchor).nodeId;
  if (!nodeId) {
    return null;
  }
  const [kind, key] = [nodeId.slice(0, nodeId.indexOf(':')), nodeId.slice(nodeId.indexOf(':') + 1)];
  const selection: EditorSelection = kind === 'stage' ? { kind: 'stage', stageKey: key } : { kind: 'gateway', gatewayKey: key };
  const transitionIndex = anchor.kind === 'Route' ? (routeOwner(blueprint, anchor.key)?.index ?? null) : null;
  return { selection, transitionIndex };
}

/** Whether a note pinned here belongs with this selection, for the Properties panel's notes. */
export function anchorMatchesSelection(blueprint: ServiceBlueprint, anchor: DesignNoteAnchor, selection: EditorSelection): boolean {
  const nodeId = placeAnchor(blueprint, anchor).nodeId;
  if (!selection) {
    return nodeId === null;
  }
  return nodeId === (selection.kind === 'stage' ? `stage:${selection.stageKey}` : `gateway:${selection.gatewayKey}`);
}
