/**
 * The editor now works directly against the persisted ServiceBlueprintDefinition
 * contract, so load/save is a straight JSON pass-through.
 */

import type { ServiceBlueprint } from './types.js';

export function serialiseServiceBlueprint(serviceBlueprint: ServiceBlueprint): Record<string, unknown> {
  return serviceBlueprint as unknown as Record<string, unknown>;
}

export function normaliseServiceBlueprint(raw: Record<string, unknown>): ServiceBlueprint {
  return raw as unknown as ServiceBlueprint;
}
