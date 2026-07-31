/**
 * Public entry point for the editor's bundles (vite.service-blueprint-editor.config.ts's
 * `wayfinder-elements` entry — see src/service-blueprint-editor/README.md).
 *
 * Importing this module registers the three public custom elements and
 * exposes the TypeScript boundary types a host needs to wire them up.
 * Everything else in service-blueprint-editor/ is composition detail, tagged
 * `@internal`, and not exported here.
 */

import './service-blueprint-editor/wayfinder-service-blueprint-editor.js';
import './service-blueprint-editor/wayfinder-service-blueprint-editor-shell.js';
import './service-blueprint-editor/wayfinder-service-blueprint-graph.js';

export { WayfinderServiceBlueprintEditorElement } from './service-blueprint-editor/wayfinder-service-blueprint-editor.js';
export { WayfinderServiceBlueprintEditorShellElement } from './service-blueprint-editor/wayfinder-service-blueprint-editor-shell.js';
export { WayfinderServiceBlueprintGraphElement } from './service-blueprint-editor/wayfinder-service-blueprint-graph.js';

export type {
  AuthoredServiceBlueprint,
  AuthoredStage,
  AuthoredGateway,
  AuthoredTransition,
  AuthoredParameterSchema,
  QueueDefinition,
  ServiceBlueprintLayoutBlock,
  ServiceBlueprintCalculationsBlock,
  ServiceBlueprintDefinitionMetadata,
} from './service-blueprint-editor/types.js';
export { hydrateServiceBlueprintDefinition } from './service-blueprint-editor/types.js';
export { serializeAuthoredServiceBlueprint } from './service-blueprint-editor/service-blueprint-canonical-json.js';

export type {
  ServiceBlueprintSource,
  ServiceBlueprintSummary,
  ServiceBlueprintSaveErrorDetail,
  ServiceBlueprintSaveErrorOptions,
} from './service-blueprint-editor/service-blueprint-source.js';
export {
  ServiceBlueprintSaveError,
  normaliseServiceBlueprintSaveError,
  sanitiseServiceBlueprintSaveErrorLines,
  sanitiseServiceBlueprintSaveErrorText,
} from './service-blueprint-editor/service-blueprint-source.js';

export type { ServiceBlueprintActionCatalog } from './service-blueprint-editor/action-catalog.js';
export { BuiltInServiceBlueprintActionCatalog } from './service-blueprint-editor/action-catalog.js';

export type { ServiceBlueprintAuthorContext } from './service-blueprint-editor/service-blueprint-author-context.js';
