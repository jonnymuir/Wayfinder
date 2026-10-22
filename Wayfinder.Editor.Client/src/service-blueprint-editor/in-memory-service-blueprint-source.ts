/**
 * In-memory reference implementation of `ServiceBlueprintSource`.
 *
 * Useful for stories, tests, and any host that wants page-lifetime persistence
 * without hooking up a backend. Hold a clone on read and clone again on save
 * so callers cannot mutate stored state through their own references. Versions
 * behave as a real store's do: a save must be made at the stored version, and
 * moves it on by one.
 */

import type { ServiceBlueprint } from './types.js';
import { ServiceBlueprintSaveError, type ServiceBlueprintSource, type ServiceBlueprintSummary } from './service-blueprint-source.js';

type SeedEntry = ServiceBlueprint | { blueprintKey: string; serviceBlueprint: ServiceBlueprint };

function deepClone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

export class InMemoryServiceBlueprintSource implements ServiceBlueprintSource {
  private readonly serviceBlueprints = new Map<string, ServiceBlueprint>();

  constructor(seed: ReadonlyArray<SeedEntry> = []) {
    for (const entry of seed) {
      if ('serviceBlueprint' in entry) {
        this.serviceBlueprints.set(entry.blueprintKey, deepClone(entry.serviceBlueprint));
      } else {
        this.serviceBlueprints.set(entry.definitionKey, deepClone(entry));
      }
    }
  }

  async list(): Promise<ServiceBlueprintSummary[]> {
    return Array.from(this.serviceBlueprints.entries())
      .map(([blueprintKey, serviceBlueprint]) => ({
        blueprintKey,
        definitionKey: serviceBlueprint.definitionKey,
        displayName: serviceBlueprint.displayName,
      }))
      .sort((a, b) => a.blueprintKey.localeCompare(b.blueprintKey));
  }

  async load(key: string): Promise<ServiceBlueprint> {
    const serviceBlueprint = this.serviceBlueprints.get(key);
    if (!serviceBlueprint) {
      throw new Error(`ServiceBlueprint "${key}" not found.`);
    }
    return deepClone(serviceBlueprint);
  }

  async save(key: string, serviceBlueprint: ServiceBlueprint): Promise<void> {
    const stored = this.serviceBlueprints.get(key);
    if (stored && stored.version !== serviceBlueprint.version) {
      throw new ServiceBlueprintSaveError({
        title: 'Changed elsewhere',
        summary: `“${key}” was changed elsewhere since you loaded it (now at version ${stored.version}).`,
        isConflict: true,
        currentVersion: stored.version,
      });
    }
    this.serviceBlueprints.set(key, deepClone({ ...serviceBlueprint, version: serviceBlueprint.version + 1 }));
  }

  async checkVersion(key: string): Promise<number | null> {
    return this.serviceBlueprints.get(key)?.version ?? null;
  }

  /** Returns the underlying entries — handy for tests that want to assert state. */
  snapshot(): ReadonlyMap<string, ServiceBlueprint> {
    return new Map(this.serviceBlueprints);
  }
}
