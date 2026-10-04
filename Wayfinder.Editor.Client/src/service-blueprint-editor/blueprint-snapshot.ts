import { hydrateServiceBlueprintDefinition } from './blueprint-hydration.js';
import type { ServiceBlueprint } from './types.js';

/** An independent, normalised copy: what history, the saved baseline and incoming loads are stored as. */
export function cloneServiceBlueprint(serviceBlueprint: ServiceBlueprint): ServiceBlueprint {
  return hydrateServiceBlueprintDefinition(JSON.parse(JSON.stringify(serviceBlueprint)) as ServiceBlueprint);
}

export function serviceBlueprintsEqual(left: ServiceBlueprint | null, right: ServiceBlueprint | null): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}
