/**
 * ServiceBlueprintComponentCatalog — the schema behind the properties panel's component
 * add/edit UI. Unlike ServiceBlueprintActionCatalog's built-in default (a hand-mirrored static
 * stub, see action-catalog.ts), this defaults to a *live* fetch from whichever host is
 * connected — so a host-registered custom component type (see
 * docs/guides/extending-the-component-catalog.md) shows up automatically, with no editor code
 * change. Hosts that genuinely have no live endpoint for this (a pure offline demo, a
 * Storybook story) can supply a `StaticServiceBlueprintComponentCatalog` instead, or leave
 * `componentCatalog` unset entirely — the add/edit UI degrades gracefully to today's read-only
 * component list when the catalog is empty.
 */

import type { ComponentDescriptor } from './types.js';

export interface ServiceBlueprintComponentCatalog {
  entries(): Promise<ComponentDescriptor[]>;
}

export interface HttpServiceBlueprintComponentCatalogOptions {
  /** Origin override for cross-origin development. Defaults to same-origin. */
  baseUrl?: string;
}

/**
 * Fetches GET {baseUrl}/wayfinder/service-blueprint-authoring/component-types once and caches
 * the result for this instance's lifetime — the catalog is process-wide, registry-freezes-on-
 * first-read state on the host side (see ComponentTypeRegistry.cs), so it can't change during a
 * single editor session.
 */
export class HttpServiceBlueprintComponentCatalog implements ServiceBlueprintComponentCatalog {
  private readonly base: string;
  private cache: Promise<ComponentDescriptor[]> | null = null;

  constructor(options: HttpServiceBlueprintComponentCatalogOptions = {}) {
    this.base = (options.baseUrl ?? '').replace(/\/$/, '');
  }

  entries(): Promise<ComponentDescriptor[]> {
    this.cache ??= this._fetch();
    return this.cache;
  }

  private async _fetch(): Promise<ComponentDescriptor[]> {
    const response = await fetch(`${this.base}/wayfinder/service-blueprint-authoring/component-types`, {
      headers: { Accept: 'application/json' },
      credentials: 'same-origin',
    });
    if (!response.ok) {
      throw new Error(`Failed to load the component type catalog (${response.status} ${response.statusText}).`);
    }
    return (await response.json()) as ComponentDescriptor[];
  }
}

/** Wraps a fixed set of descriptors — for tests and Storybook stories with no live host. */
export class StaticServiceBlueprintComponentCatalog implements ServiceBlueprintComponentCatalog {
  constructor(private readonly descriptors: ComponentDescriptor[]) {}

  async entries(): Promise<ComponentDescriptor[]> {
    return this.descriptors;
  }
}
