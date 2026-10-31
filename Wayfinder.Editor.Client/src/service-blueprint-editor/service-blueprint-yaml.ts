import { type Document, isAlias, isNode, LineCounter, type Node, parseDocument, stringify, visit } from 'yaml';
import { hydrateServiceBlueprintDefinition } from './blueprint-hydration.js';
import { type YamlShape, yamlDiscriminators, yamlShapes } from './generated/wayfinder-model.js';
import type {
  NodePosition,
  ServiceBlueprintGatewayDefinition,
  ServiceBlueprintLayoutDefinition,
  ServiceBlueprintRouteDefinition,
  StageDefinition,
  ServiceBlueprintStageValidationRule,
  ServiceBlueprint,
  QueueDefinition,
} from './types.js';

/** A serialiser must name every property of the model it writes, so a property added to the C# model cannot be dropped silently. */
type Serialised<T> = { [K in keyof T]-?: unknown };

/**
 * A blueprint as YAML text, the way the Definition tab, the clipboard and a host's routes carry it.
 * Writing is canonical (the same blueprint always gives the same text) and keys come in the order the
 * server's ServiceBlueprintYaml writes them (`yamlShapes`, generated from the C# model). Reading
 * refuses anchors and aliases, as the server does.
 */
export type TopLevelFieldKind = 'string' | 'number' | 'boolean' | 'array' | 'object';

/**
 * Every top-level property the editor owns: it is read from a pasted/typed definition, kept
 * through hydrate, and written back by the serialiser. This is the one place a new top-level field
 * is declared; the lint's type checks and the Definition-tab coerce derive from it, and service-blueprint-top-level-fields.test.ts fails if a field here
 * is dropped anywhere along that path, or if the C# ServiceBlueprint gains a property nobody
 * has decided about.
 */
export const EDITOR_TOP_LEVEL_FIELDS = {
  definitionKey: 'string',
  displayName: 'string',
  version: 'number',
  initialStage: 'string',
  requestPolicy: 'string',
  allowManualRestart: 'boolean',
  description: 'string',
  schemaVersion: 'string',
  authoredServiceBlueprintId: 'string',
  calculations: 'object',
  queues: 'array',
  stages: 'array',
  gateways: 'array',
  layout: 'object',
  handoffs: 'array',
  tags: 'object',
} as const satisfies Record<keyof ServiceBlueprint, TopLevelFieldKind>;

export function matchesTopLevelFieldKind(value: unknown, kind: TopLevelFieldKind): boolean {
  switch (kind) {
    case 'array':
      return Array.isArray(value);
    case 'object':
      return typeof value === 'object' && value !== null && !Array.isArray(value);
    default:
      return typeof value === kind;
  }
}

function serialisableRoute(route: ServiceBlueprintRouteDefinition): Serialised<ServiceBlueprintRouteDefinition> {
  return {
    id: route.id,
    target: route.target,
    trigger: route.trigger,
    label: route.label,
    style: route.style,
    showWhen: route.showWhen,
    requiresRole: route.requiresRole,
    actions: route.actions,
  };
}

function serialisableStageValidation(rule: ServiceBlueprintStageValidationRule): Serialised<ServiceBlueprintStageValidationRule> {
  return {
    code: rule.code,
    when: rule.when,
    rule: rule.rule,
    field: rule.field,
    message: rule.message,
    actions: rule.actions,
  };
}

function serialisableQueue(queue: QueueDefinition): Serialised<QueueDefinition> {
  return {
    key: queue.key,
    displayName: queue.displayName,
    description: queue.description,
    actor: queue.actor,
    roleGates: queue.roleGates?.length ? queue.roleGates : undefined,
    assignmentPolicy: queue.assignmentPolicy,
    owningTeamId: queue.owningTeamId,
    tags: queue.tags && Object.keys(queue.tags).length > 0 ? queue.tags : undefined,
  };
}

function serialisableState(stage: StageDefinition): Serialised<StageDefinition> {
  return {
    stageKey: stage.stageKey,
    displayName: stage.displayName,
    components: stage.components ?? [],
    description: stage.description,
    stageType: stage.stageType,
    actor: stage.actor,
    queueKey: stage.queueKey,
    routes: (stage.routes ?? []).map(serialisableRoute),
    actions: stage.actions,
    roleGates: stage.roleGates,
    icon: stage.icon,
    validations: (stage.validations ?? []).length > 0 ? (stage.validations ?? []).map(serialisableStageValidation) : undefined,
  };
}

function serialisableGateway(gateway: ServiceBlueprintGatewayDefinition): Serialised<ServiceBlueprintGatewayDefinition> {
  return {
    key: gateway.key,
    displayName: gateway.displayName,
    description: gateway.description,
    gatewayType: gateway.gatewayType,
    queueKey: gateway.queueKey,
    actor: gateway.actor,
    roleGates: gateway.roleGates,
    routes: (gateway.routes ?? []).map(serialisableRoute),
    waitingContent: gateway.waitingContent,
    waitingPace: gateway.waitingPace,
    waitingExpectedSeconds: gateway.waitingExpectedSeconds,
    waitingPollIntervalMs: gateway.waitingPollIntervalMs,
    waitingIcon: gateway.waitingIcon,
    waitingAllowDefer: gateway.waitingAllowDefer,
    waitingDeferMessage: gateway.waitingDeferMessage,
    requiredIncomingQueues: gateway.requiredIncomingQueues,
    icon: gateway.icon,
  };
}

function serialisableServiceBlueprint(serviceBlueprint: ServiceBlueprint): Serialised<ServiceBlueprint> {
  return {
    definitionKey: serviceBlueprint.definitionKey,
    displayName: serviceBlueprint.displayName,
    version: serviceBlueprint.version,
    initialStage: serviceBlueprint.initialStage,
    requestPolicy: serviceBlueprint.requestPolicy,
    allowManualRestart: serviceBlueprint.allowManualRestart ? true : undefined,
    description: serviceBlueprint.description,
    schemaVersion: serviceBlueprint.schemaVersion,
    authoredServiceBlueprintId: serviceBlueprint.authoredServiceBlueprintId,
    queues: (serviceBlueprint.queues ?? []).map(serialisableQueue),
    stages: serviceBlueprint.stages.map(serialisableState),
    gateways: (serviceBlueprint.gateways ?? []).map(serialisableGateway),
    calculations: serviceBlueprint.calculations,
    layout: serialisableLayout(serviceBlueprint.layout),
    handoffs: serviceBlueprint.handoffs?.length ? serviceBlueprint.handoffs : undefined,
    tags: serviceBlueprint.tags && Object.keys(serviceBlueprint.tags).length > 0 ? serviceBlueprint.tags : undefined,
  };
}

function wholePixels(positions: Record<string, NodePosition> | undefined): Record<string, NodePosition> | undefined {
  const entries = Object.entries(positions ?? {});
  if (entries.length === 0) {
    return undefined;
  }
  // Whole pixels only: drag jitter must never produce spurious dirty state.
  return Object.fromEntries(entries.map(([key, position]) => [key, { x: Math.round(position.x), y: Math.round(position.y) }]));
}

function serialisableLayout(
  layout: ServiceBlueprintLayoutDefinition | undefined
): Serialised<ServiceBlueprintLayoutDefinition> | undefined {
  const nodes = wholePixels(layout?.nodes);
  const routes = wholePixels(layout?.routes);
  return nodes || routes ? { nodes, routes } : undefined;
}

/** The shape a record is written with: its own type's, or that of the concrete type its discriminator names. */
function shapeOf(record: Record<string, unknown>, typeName: string | undefined): YamlShape | undefined {
  const discriminator = typeName ? yamlDiscriminators[typeName] : undefined;
  const concrete = discriminator ? discriminator.types[String(record[discriminator.property])] : typeName;
  return concrete ? yamlShapes[concrete] : undefined;
}

function keysInOrder(record: Record<string, unknown>, shape: YamlShape | undefined): string[] {
  return shape ? [...shape.keys, ...Object.keys(record).filter((key) => !shape.keys.includes(key))] : Object.keys(record);
}

function orderedEntry(item: unknown, shape: YamlShape | undefined, key: string): unknown {
  const mapOf = shape?.maps[key];
  if (!mapOf) {
    return ordered(item, shape?.nested[key]);
  }
  return Object.fromEntries(Object.entries(item as Record<string, unknown>).map(([entry, inner]) => [entry, ordered(inner, mapOf)]));
}

function orderedRecord(record: Record<string, unknown>, typeName: string | undefined): Record<string, unknown> {
  const shape = shapeOf(record, typeName);
  const result: Record<string, unknown> = {};
  for (const key of keysInOrder(record, shape)) {
    if (record[key] !== undefined && record[key] !== null) {
      result[key] = orderedEntry(record[key], shape, key);
    }
  }
  return result;
}

/** Keys in the order `typeName` is written in, nested values ordered by their own types; nulls left out. */
function ordered(value: unknown, typeName: string | undefined): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => ordered(item, typeName));
  }
  return value && typeof value === 'object' ? orderedRecord(value as Record<string, unknown>, typeName) : value;
}

/** Writes YAML the way ServiceBlueprintYaml does: two-space indents, indented lists, multi-line text as `|` blocks, no anchors. */
export function stringifyYaml(value: unknown): string {
  return stringify(value, { indent: 2, indentSeq: true, lineWidth: 0, blockQuote: 'literal', aliasDuplicateObjects: false });
}

export function serializeAuthoredServiceBlueprint(serviceBlueprint: ServiceBlueprint): string {
  return stringifyYaml(ordered(serialisableServiceBlueprint(serviceBlueprint), 'ServiceBlueprint'));
}

/** Writes any part of a blueprint (a stage, a gateway, a component) as YAML, in its type's key order. */
export function serializeBlueprintPart(value: unknown, typeName: string): string {
  return stringifyYaml(ordered(value, typeName));
}

export function authoredServiceBlueprintEquals(left: ServiceBlueprint | null, right: ServiceBlueprint | null): boolean {
  if (!left && !right) {
    return true;
  }
  if (!left || !right) {
    return false;
  }
  return serializeAuthoredServiceBlueprint(left) === serializeAuthoredServiceBlueprint(right);
}

const isAnchorOrAlias = (node: unknown): boolean => isAlias(node) || Boolean(isNode(node) && node.anchor);

/** The line of the first anchor (`&`) or alias (`*`) in a document, if it has one. */
function firstAnchorOrAlias(document: Document, lineCounter: LineCounter): number | undefined {
  let line: number | undefined;
  visit(document, (_key, node) => {
    if (!isAnchorOrAlias(node)) {
      return undefined;
    }
    line = lineCounter.linePos((node as Node).range?.[0] ?? 0).line;
    return visit.BREAK;
  });
  return line;
}

export type ParsedYaml = { ok: true; value: unknown } | { ok: false; message: string; line: number };

/**
 * Reads YAML text (a whole blueprint or a part of one). A syntax error, a duplicate key, or an
 * anchor or alias (`&`, `*`, which the server refuses because they let a small document expand
 * without bound) is reported with its 1-based line.
 */
export function parseYaml(text: string): ParsedYaml {
  const lineCounter = new LineCounter();
  const document = parseDocument(text, { lineCounter, uniqueKeys: true, prettyErrors: false });
  const [error] = document.errors;
  if (error) {
    return { ok: false, message: error.message.split('\n')[0], line: lineCounter.linePos(error.pos[0]).line };
  }
  const aliasAt = firstAnchorOrAlias(document, lineCounter);
  if (aliasAt !== undefined) {
    return { ok: false, message: 'Anchors and aliases (& and *) are not allowed in a service blueprint.', line: aliasAt };
  }
  return { ok: true, value: document.toJS() };
}

/** Reads a whole blueprint a host sent as YAML, ready for the editor. Throws, naming the line, if the text cannot be read. */
export function parseServiceBlueprintYaml(text: string): ServiceBlueprint {
  const parsed = parseYaml(text);
  if (!parsed.ok) {
    throw new Error(`The service blueprint's YAML could not be read (line ${parsed.line}): ${parsed.message}`);
  }
  return hydrateServiceBlueprintDefinition(parsed.value as ServiceBlueprint);
}
