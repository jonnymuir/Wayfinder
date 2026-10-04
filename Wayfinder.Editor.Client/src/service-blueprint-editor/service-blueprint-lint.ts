import { EDITOR_TOP_LEVEL_FIELDS, matchesTopLevelFieldKind } from './service-blueprint-canonical-json.js';
import type { ComponentDescriptor, ServiceBlueprint } from './types.js';
import { hydrateServiceBlueprintDefinition } from './blueprint-hydration.js';
import { DocumentLinter } from './lint/document-lint.js';
import type { DefinitionLint } from './lint/lint-support.js';

export type { DefinitionLint };

/** Problems with a hand-edited Definition document, each with a path hint and (where findable) a source line. */
export function lintAuthoredServiceBlueprintDocument(
  parsed: unknown,
  source: string,
  componentCatalog: ComponentDescriptor[] = []
): DefinitionLint[] {
  return new DocumentLinter(source, componentCatalog).lint(parsed);
}

export function coerceParsedAuthoredServiceBlueprint(parsed: unknown): ServiceBlueprint {
  const root = parsed as Record<string, unknown>;
  // Copied from the field table, not field by field, so a newly declared top-level property cannot
  // be forgotten here (allowManualRestart was, and was dropped on every Definition-tab apply).
  const owned = Object.fromEntries(
    Object.entries(EDITOR_TOP_LEVEL_FIELDS)
      .filter(([key, kind]) => matchesTopLevelFieldKind(root[key], kind))
      .map(([key]) => [key, root[key]])
  );
  return hydrateServiceBlueprintDefinition({
    definitionKey: '',
    displayName: '',
    version: 1,
    initialStage: '',
    requestPolicy: 'single',
    queues: [],
    stages: [],
    gateways: [],
    ...owned,
  } as unknown as ServiceBlueprint);
}
