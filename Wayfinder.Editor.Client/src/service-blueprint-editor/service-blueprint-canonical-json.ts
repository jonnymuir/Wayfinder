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
 * Stable, deterministic JSON serialization for the flattened serviceBlueprint definition
 * used by the Definition tab.
 */
export type TopLevelFieldKind = 'string' | 'number' | 'boolean' | 'array' | 'object';

/**
 * Every top-level property the editor owns: it is read from a pasted/typed definition, kept
 * through hydrate, and written back by the serialiser, in this order. This is the one place a new
 * top-level field is declared; the lint's type checks, the Definition-tab coerce and the canonical
 * key order all derive from it, and service-blueprint-top-level-fields.test.ts fails if a field here
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

const TOP_LEVEL_KEY_ORDER: readonly string[] = Object.keys(EDITOR_TOP_LEVEL_FIELDS);

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
    waitingExpectedSeconds: gateway.waitingExpectedSeconds,
    waitingPollIntervalMs: gateway.waitingPollIntervalMs,
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

function orderTopLevel(value: Record<string, unknown>): Record<string, unknown> {
  const ordered: Record<string, unknown> = {};
  for (const key of TOP_LEVEL_KEY_ORDER) {
    if (key in value && value[key] !== undefined) {
      ordered[key] = value[key];
    }
  }
  for (const key of Object.keys(value).sort()) {
    if (!(key in ordered) && value[key] !== undefined) {
      ordered[key] = value[key];
    }
  }
  return ordered;
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }
  if (value && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const sorted: Record<string, unknown> = {};
    // PrismComponent (Wayfinder.Models.ServiceDesign.Components) is a polymorphic type discriminated by "type".
    // System.Text.Json's built-in polymorphic deserialization requires that discriminator
    // to be the first property in the JSON object, so it must survive alphabetical sorting.
    if (record.type !== undefined) {
      sorted.type = sortKeys(record.type);
    }
    for (const key of Object.keys(record).sort()) {
      if (key === 'type') {
        continue;
      }
      if (record[key] !== undefined) {
        sorted[key] = sortKeys(record[key]);
      }
    }
    return sorted;
  }
  return value;
}

export function serializeAuthoredServiceBlueprint(serviceBlueprint: ServiceBlueprint): string {
  const top = orderTopLevel(serialisableServiceBlueprint(serviceBlueprint));
  const canonical: Record<string, unknown> = {};
  for (const key of Object.keys(top)) {
    // `calculations` is deliberately NOT run through sortKeys. calculations.fields' own key
    // order IS the declaration/evaluation order (docs/guides/calculation-language.md: "Fields
    // are evaluated once, in declaration order" — a forward reference is a hard error) —
    // calculation-ordering.ts computes and preserves that order deliberately when the
    // Calculations tab authors it. Alphabetising it here would silently reorder any blueprint
    // whose fields rely on evaluation order, which is effectively all of them — found live: a
    // save through this exact path turned a working calculation set into one where nearly
    // every field errored with "Unknown name", since the field it depended on now sorted after
    // it. tables/series don't strictly require this, but are left untouched for the same
    // reason: this key isn't a "make it comparable" concern the way stage/gateway shape is —
    // it's real, order-sensitive content the author (human or the ordering algorithm) controls.
    canonical[key] = key === 'calculations' ? top[key] : sortKeys(top[key]);
  }
  return JSON.stringify(canonical, null, 2);
}

export function authoredServiceBlueprintJsonEquals(left: ServiceBlueprint | null, right: ServiceBlueprint | null): boolean {
  if (!left && !right) {
    return true;
  }
  if (!left || !right) {
    return false;
  }
  return serializeAuthoredServiceBlueprint(left) === serializeAuthoredServiceBlueprint(right);
}
