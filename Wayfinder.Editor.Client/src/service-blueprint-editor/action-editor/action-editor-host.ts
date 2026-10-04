import type { ReactiveControllerHost } from 'lit';
import type { ActionCatalogEntry, ActionDefinition, SupportSystemDescriptor } from '../types.js';
import type { ActionEditorTarget } from '../action-editing.js';
import type { FieldReference } from '../component-property-references.js';

/** What the action editor's controllers read from, and report changes through, the element hosting them. */
export interface ActionEditorHost extends ReactiveControllerHost, HTMLElement {
  readonly actions: ActionDefinition[];
  readonly actionCatalog: ActionCatalogEntry[];
  readonly supportSystemCatalog: SupportSystemDescriptor[];
  readonly supportSystemFieldReferences: FieldReference[];
  readonly target: ActionEditorTarget;
  readonly subjectLabel: string;
  readonly selectedActionIndex: number | null;
  announce(message: string): void;
  emitActionsUpdated(actions: ActionDefinition[]): void;
  setSelectedAction(index: number | null): void;
  actionEntry(action: ActionDefinition): ActionCatalogEntry | null;
  actionLabel(action: ActionDefinition): string;
}

/** How a parameter editor changes the action it is editing. */
export interface ParamEditing {
  updateParam(index: number, key: string, value: unknown): void;
  updateParams(index: number, params: Record<string, unknown>): void;
  announce(message: string): void;
}
