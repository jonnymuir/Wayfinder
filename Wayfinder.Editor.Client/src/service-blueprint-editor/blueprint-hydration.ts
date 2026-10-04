import type {
  ActionDefinition,
  Component,
  GatewayKind,
  HandoffDefinition,
  NodePosition,
  QueueDefinition,
  ServiceBlueprint,
  ServiceBlueprintCalculationSet,
  ServiceBlueprintGatewayDefinition,
  ServiceBlueprintLayoutDefinition,
  ServiceBlueprintRouteDefinition,
  ServiceBlueprintStageValidationRule,
  StageDefinition,
  StageKind,
} from './types.js';

/**
 * Parses an authored blueprint into the editor's model: required lists exist, strings are
 * trimmed, duplicate routes are dropped. There is one wire shape (the C# ServiceBlueprint's),
 * so this reads each property under its one name. It runs again over an already-hydrated
 * blueprint on every edit, so it must be idempotent and must not delete a half-typed entry.
 */
/** Every property of T must be produced, with its real type: a property added to the C# model cannot be dropped here silently. */
type OptionalKeys<T> = { [K in keyof T]-?: Record<never, never> extends Pick<T, K> ? K : never }[keyof T];
type Complete<T> = { [K in Exclude<keyof T, OptionalKeys<T>>]: T[K] } & { [K in OptionalKeys<T>]: T[K] | undefined };

export function hydrateServiceBlueprintDefinition(serviceBlueprint: unknown): ServiceBlueprint {
  const root = asRecord(serviceBlueprint);
  const queues = dedupeByKey(
    asArray<Record<string, unknown>>(root.queues)
      .map(normaliseQueueDefinition)
      .filter((queue): queue is Complete<QueueDefinition> => queue !== null),
    (queue) => queue.key
  );
  const stages = asArray<Record<string, unknown>>(root.stages).map(normaliseStage);
  const handoffs = asArray<HandoffDefinition>(root.handoffs);
  const tags = asRecord(root.tags) as Record<string, string>;

  const hydrated: Complete<ServiceBlueprint> = {
    definitionKey: typeof root.definitionKey === 'string' ? root.definitionKey : '',
    displayName: typeof root.displayName === 'string' ? root.displayName : '',
    version: typeof root.version === 'number' ? root.version : 1,
    initialStage: firstString(root.initialStage) ?? stages[0]?.stageKey ?? '',
    requestPolicy: typeof root.requestPolicy === 'string' ? root.requestPolicy : 'single',
    allowManualRestart: root.allowManualRestart === true,
    description: firstString(root.description),
    schemaVersion: firstString(root.schemaVersion),
    authoredServiceBlueprintId: firstString(root.authoredServiceBlueprintId),
    queues,
    stages,
    gateways: asArray<Record<string, unknown>>(root.gateways).map(normaliseGateway),
    calculations:
      root.calculations && typeof root.calculations === 'object' && !Array.isArray(root.calculations)
        ? (root.calculations as ServiceBlueprintCalculationSet)
        : undefined,
    layout: sanitiseLayoutBlock(root.layout),
    handoffs: handoffs.length > 0 ? handoffs : undefined,
    tags: Object.keys(tags).length > 0 ? tags : undefined,
  };
  return hydrated;
}

function sanitisePositionRecord(value: unknown): Record<string, NodePosition> {
  const record = asRecord(value);
  const entries: Record<string, NodePosition> = {};
  for (const [key, raw] of Object.entries(record)) {
    const position = asRecord(raw);
    if (typeof position.x === 'number' && Number.isFinite(position.x) && typeof position.y === 'number' && Number.isFinite(position.y)) {
      entries[key] = { x: position.x, y: position.y };
    }
  }
  return entries;
}

function sanitiseLayoutBlock(value: unknown): ServiceBlueprintLayoutDefinition | undefined {
  const record = asRecord(value);
  const nodes = sanitisePositionRecord(record.nodes);
  const routes = sanitisePositionRecord(record.routes);
  const block: ServiceBlueprintLayoutDefinition = {};
  if (Object.keys(nodes).length > 0) {
    block.nodes = nodes;
  }
  if (Object.keys(routes).length > 0) {
    block.routes = routes;
  }
  return Object.keys(block).length > 0 ? block : undefined;
}

function firstString(...values: unknown[]): string | undefined {
  for (const value of values) {
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }
  }
  return undefined;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function asArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

function asStringArray(value: unknown): string[] {
  return asArray<unknown>(value)
    .filter((entry): entry is string => typeof entry === 'string' && entry.trim().length > 0)
    .map((entry) => entry.trim());
}

function dedupeByKey<T>(items: T[], keyFor: (item: T) => string): T[] {
  const seen = new Set<string>();
  return items.filter((item) => {
    const key = keyFor(item);
    if (!key || seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

function normaliseQueueDefinition(rawQueue: Record<string, unknown>): Complete<QueueDefinition> | null {
  const key = firstString(rawQueue.key);
  if (!key) {
    return null;
  }
  return {
    key,
    displayName: firstString(rawQueue.displayName) ?? key,
    description: firstString(rawQueue.description),
    actor: firstString(rawQueue.actor),
    roleGates: asStringArray(rawQueue.roleGates),
    assignmentPolicy: firstString(rawQueue.assignmentPolicy),
    owningTeamId: firstString(rawQueue.owningTeamId),
    tags: asRecord(rawQueue.tags) as Record<string, string>,
  };
}

function routeId(sourceKey: string, trigger: string, targetKey: string) {
  return `${sourceKey || 'unknown'}--${trigger || 'continue'}--${targetKey || 'unknown'}`;
}

function normaliseRoute(rawRoute: Record<string, unknown>, sourceKey: string): Complete<ServiceBlueprintRouteDefinition> {
  const trigger = firstString(rawRoute.trigger) ?? 'continue';
  const target = firstString(rawRoute.target) ?? '';
  return {
    id: firstString(rawRoute.id) ?? routeId(sourceKey, trigger, target),
    target,
    trigger,
    label: firstString(rawRoute.label),
    style: firstString(rawRoute.style),
    showWhen: firstString(rawRoute.showWhen),
    requiresRole: firstString(rawRoute.requiresRole),
    actions: asArray<ActionDefinition>(rawRoute.actions),
  };
}

function normaliseRoutes(rawRoutes: unknown, sourceKey: string): ServiceBlueprintRouteDefinition[] {
  return dedupeByKey(
    asArray<Record<string, unknown>>(rawRoutes).map((route) => normaliseRoute(route, sourceKey)),
    (route) => route.id
  );
}

function normaliseStage(rawStage: Record<string, unknown>): Complete<StageDefinition> {
  const stageKey = firstString(rawStage.stageKey) ?? '';
  return {
    stageKey,
    displayName: firstString(rawStage.displayName) ?? stageKey,
    components: asArray<Component>(rawStage.components),
    description: firstString(rawStage.description),
    stageType: (firstString(rawStage.stageType) as StageKind | undefined) ?? 'Question',
    actor: firstString(rawStage.actor),
    queueKey: firstString(rawStage.queueKey) ?? '',
    routes: normaliseRoutes(rawStage.routes, stageKey),
    actions: asArray<ActionDefinition>(rawStage.actions),
    roleGates: asStringArray(rawStage.roleGates),
    icon: firstString(rawStage.icon),
    validations: normaliseStageValidations(rawStage.validations),
  };
}

function normaliseStageValidations(value: unknown): Complete<ServiceBlueprintStageValidationRule>[] {
  // No "drop if empty" filter here, deliberately — the shell round-trips serviceBlueprint through
  // this exact normalisation on every edit (see hydrateServiceBlueprintDefinition's callers), so
  // filtering out an all-blank entry would delete a rule the instant it's added, before an author
  // has typed anything into it. Same tolerance routes/fields already get mid-edit.
  return asArray<Record<string, unknown>>(value).map((raw) => ({
    code: firstString(raw.code) ?? '',
    when: firstString(raw.when),
    rule: firstString(raw.rule) ?? '',
    field: firstString(raw.field),
    message: firstString(raw.message) ?? '',
    actions: Array.isArray(raw.actions) ? asStringArray(raw.actions) : undefined,
  }));
}

function normaliseGateway(rawGateway: Record<string, unknown>): Complete<ServiceBlueprintGatewayDefinition> {
  const key = firstString(rawGateway.key) ?? '';
  return {
    key,
    displayName: firstString(rawGateway.displayName) ?? key,
    description: firstString(rawGateway.description),
    gatewayType: (firstString(rawGateway.gatewayType) as GatewayKind | undefined) ?? 'Split',
    queueKey: firstString(rawGateway.queueKey) ?? '',
    actor: firstString(rawGateway.actor),
    roleGates: asStringArray(rawGateway.roleGates),
    routes: normaliseRoutes(rawGateway.routes, key),
    waitingContent: firstString(rawGateway.waitingContent),
    waitingExpectedSeconds: typeof rawGateway.waitingExpectedSeconds === 'number' ? rawGateway.waitingExpectedSeconds : 0,
    waitingPollIntervalMs: typeof rawGateway.waitingPollIntervalMs === 'number' ? rawGateway.waitingPollIntervalMs : 0,
    waitingAllowDefer: rawGateway.waitingAllowDefer === true,
    waitingDeferMessage: firstString(rawGateway.waitingDeferMessage),
    requiredIncomingQueues: asStringArray(rawGateway.requiredIncomingQueues),
    icon: firstString(rawGateway.icon),
  };
}
