import type { ServiceBlueprintGatewayDefinition, StageDefinition, ServiceBlueprint } from './types.js';
import { serviceBlueprintGateways, serviceBlueprintQueues } from './types.js';

export type StageSurface = 'front-stage' | 'back-stage';
export interface QueueDefinition {
  queueName: string;
  displayName?: string;
  description?: string;
}

type QueueAssignedNode = StageDefinition | ServiceBlueprintGatewayDefinition;

const FRONT_STAGE_ACTORS = new Set(['applicant', 'resident', 'member', 'citizen', 'customer', 'public']);
const BACK_STAGE_ACTORS = new Set(['reviewer', 'caseworker', 'officer', 'administrator', 'admin', 'system']);

export function normaliseQueueKey(value: string | null | undefined): string {
  return value?.trim().toLowerCase() ?? '';
}

export function humaniseAssignmentLabel(value: string): string {
  return value
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function stageSurface(stage: QueueAssignedNode): StageSurface {
  const roleGates = stage.roleGates ?? [];
  if (roleGates.length > 0) {
    return 'back-stage';
  }

  const actor = normaliseQueueKey(stage.actor);
  if (!actor) {
    return 'front-stage';
  }

  if (BACK_STAGE_ACTORS.has(actor)) {
    return 'back-stage';
  }

  if (FRONT_STAGE_ACTORS.has(actor)) {
    return 'front-stage';
  }

  return actor.includes('review') || actor.includes('case') || actor.includes('system')
    ? 'back-stage'
    : 'front-stage';
}

export function stageQueueKey(stage: QueueAssignedNode): string {
  const explicitQueue = normaliseQueueKey(stage.queueKey);
  if (explicitQueue) {
    return explicitQueue;
  }

  const gatedRole = (stage.roleGates ?? []).find(value => value.trim());
  if (gatedRole) {
    return normaliseQueueKey(gatedRole);
  }

  const actor = normaliseQueueKey(stage.actor);
  if (actor) {
    return actor;
  }

  return stageSurface(stage) === 'back-stage' ? 'reviewer' : 'public';
}

export function stageQueueLabel(
  serviceBlueprint: Pick<ServiceBlueprint, 'queues'> | null | undefined,
  queueKey: string,
  availableQueues: ReadonlyArray<QueueDefinition> = []
): string {
  const normalised = normaliseQueueKey(queueKey);
  const configuredQueue = availableQueues.find(queue => normaliseQueueKey(queue.queueName) === normalised);
  if (configuredQueue?.displayName?.trim()) {
    return configuredQueue.displayName.trim();
  }

  const serviceBlueprintQueue = serviceBlueprintQueues(serviceBlueprint).find(queue => normaliseQueueKey(queue.key) === normalised);
  return serviceBlueprintQueue?.displayName?.trim() || humaniseAssignmentLabel(normalised);
}

export function stageQueueDescription(
  serviceBlueprint: Pick<ServiceBlueprint, 'queues'> | null | undefined,
  queueKey: string,
  availableQueues: ReadonlyArray<QueueDefinition> = []
): string {
  const normalised = normaliseQueueKey(queueKey);
  const configuredQueue = availableQueues.find(queue => normaliseQueueKey(queue.queueName) === normalised);
  if (configuredQueue?.description?.trim()) {
    return configuredQueue.description.trim();
  }

  return `Stages and gateways in the ${stageQueueLabel(serviceBlueprint, queueKey, availableQueues)} queue`;
}

export function applyQueueToStage(stage: StageDefinition, queueKey: string): StageDefinition {
  const normalisedQueueKey = normaliseQueueKey(queueKey);

  if (!normalisedQueueKey) {
    return { ...stage, queueKey: '', actor: undefined, roleGates: [] };
  }

  const inferredActor = normalisedQueueKey.includes('business')
    ? 'reviewer'
    : normalisedQueueKey.includes('system')
      ? 'system'
      : normalisedQueueKey.includes('review')
        ? 'reviewer'
        : normalisedQueueKey;
  const usesRoleGate = !FRONT_STAGE_ACTORS.has(inferredActor);
  return { ...stage, queueKey: normalisedQueueKey, actor: inferredActor, roleGates: usesRoleGate ? [inferredActor] : [] };
}

export function serviceBlueprintQueueOptions(
  serviceBlueprint: Pick<ServiceBlueprint, 'queues' | 'stages' | 'gateways'> | null | undefined,
  availableQueues: ReadonlyArray<QueueDefinition> = []
): string[] {
  const queueKeys = new Set<string>();

  availableQueues.forEach(queue => {
    const key = normaliseQueueKey(queue.queueName);
    if (key) {
      queueKeys.add(key);
    }
  });

  serviceBlueprintQueues(serviceBlueprint).forEach(queue => {
    const key = normaliseQueueKey(queue.key);
    if (key) {
      queueKeys.add(key);
    }
  });

  serviceBlueprint?.stages?.forEach(stage => {
    const key = stageQueueKey(stage);
    if (key) {
      queueKeys.add(key);
    }
  });

  serviceBlueprintGateways(serviceBlueprint).forEach(gateway => {
    const key = stageQueueKey(gateway);
    if (key) {
      queueKeys.add(key);
    }
  });

  return [...queueKeys];
}
