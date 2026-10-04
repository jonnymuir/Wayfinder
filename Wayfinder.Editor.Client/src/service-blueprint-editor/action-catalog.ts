/**
 * ServiceBlueprintActionCatalog — host-extensible source for the action types the
 * editor knows how to render. Wayfinder ships a built-in catalog covering generic
 * actions; hosts compose their own catalog if they ship extra action types.
 */

import type { ActionCatalogEntry } from './types.js';
import { BUILT_IN_ACTION_CATALOG } from './action-catalog-entries.js';

export interface ServiceBlueprintActionCatalog {
  entries(): Promise<ActionCatalogEntry[]>;
}

/** The generic action types Wayfinder ships out of the box (see `action-catalog-entries.ts`). */
export class BuiltInServiceBlueprintActionCatalog implements ServiceBlueprintActionCatalog {
  async entries(): Promise<ActionCatalogEntry[]> {
    return BUILT_IN_ACTION_CATALOG.map(entry => JSON.parse(JSON.stringify(entry)) as ActionCatalogEntry);
  }
}
