/**
 * ServiceBlueprintSupportSystemCatalog — the schema behind a `support-system-call` action's
 * support-system/capability pickers. Same "fetched live, not a hand-mirrored stub" reasoning as
 * ServiceBlueprintComponentCatalog (see component-catalog.ts): a host-registered support system
 * (see docs/guides/support-systems.md) shows up automatically, with no editor code change. Hosts
 * with no live endpoint (a pure offline demo, a Storybook story) can supply a
 * `StaticServiceBlueprintSupportSystemCatalog` instead, or leave `supportSystemCatalog` unset —
 * the picker degrades gracefully to an empty list.
 */

import type { SupportSystemDescriptor } from './types.js';

export interface ServiceBlueprintSupportSystemCatalog {
  entries(): Promise<SupportSystemDescriptor[]>;
}

export interface HttpServiceBlueprintSupportSystemCatalogOptions {
  /** Origin override for cross-origin development. Defaults to same-origin. */
  baseUrl?: string;
}

/**
 * Fetches GET {baseUrl}/wayfinder/service-blueprint-authoring/support-systems once and caches
 * the result for this instance's lifetime — the catalog is process-wide, registry-freezes-on-
 * first-read state on the host side (see SupportSystemRegistry.cs), so it can't change during a
 * single editor session.
 */
export class HttpServiceBlueprintSupportSystemCatalog implements ServiceBlueprintSupportSystemCatalog {
  private readonly base: string;
  private cache: Promise<SupportSystemDescriptor[]> | null = null;

  constructor(options: HttpServiceBlueprintSupportSystemCatalogOptions = {}) {
    this.base = (options.baseUrl ?? '').replace(/\/$/, '');
  }

  entries(): Promise<SupportSystemDescriptor[]> {
    this.cache ??= this._fetch();
    return this.cache;
  }

  private async _fetch(): Promise<SupportSystemDescriptor[]> {
    const response = await fetch(`${this.base}/wayfinder/service-blueprint-authoring/support-systems`, {
      headers: { Accept: 'application/json' },
      credentials: 'same-origin',
    });
    if (!response.ok) {
      throw new Error(`Failed to load the support system catalog (${response.status} ${response.statusText}).`);
    }
    return (await response.json()) as SupportSystemDescriptor[];
  }
}

/** Wraps a fixed set of descriptors — for tests and Storybook stories with no live host. */
export class StaticServiceBlueprintSupportSystemCatalog implements ServiceBlueprintSupportSystemCatalog {
  constructor(private readonly descriptors: SupportSystemDescriptor[]) {}

  async entries(): Promise<SupportSystemDescriptor[]> {
    return this.descriptors;
  }
}
