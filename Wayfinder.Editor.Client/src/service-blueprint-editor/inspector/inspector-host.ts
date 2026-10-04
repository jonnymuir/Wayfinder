import type { ReactiveControllerHost } from 'lit';
import type {
  ActionCatalogEntry,
  ActionDefinition,
  ComponentDescriptor,
  ServiceBlueprint,
  ServiceBlueprintGatewayDefinition,
  StageDefinition,
  SupportSystemDescriptor,
} from '../types.js';
import type { QueueDefinition } from '../stage-assignment.js';

export type InspectorSelection = { kind: 'stage' | 'gateway'; stageKey?: string; gatewayKey?: string };

export type ActionsUpdatedDetail = {
  actions: ActionDefinition[];
};

export type ActionSelectedDetail = {
  index: number | null;
  target: 'stage' | 'transition';
  transitionIndex?: number;
};

/** What the inspector's stage, gateway and route controllers read from, and report edits through, the element hosting them. */
export interface InspectorContext {
  readonly serviceBlueprint: ServiceBlueprint | null;
  readonly selectedGatewayKey: string | null;
  readonly selectedStage: StageDefinition | null;
  readonly selectedGateway: ServiceBlueprintGatewayDefinition | null;
  readonly actionCatalog: ActionCatalogEntry[];
  readonly componentCatalog: ComponentDescriptor[];
  readonly supportSystemCatalog: SupportSystemDescriptor[];
  readonly availableQueues: QueueDefinition[];
  readonly selectedActionIndex: number | null;
  readonly selectedActionTransitionIndex: number | null;
  /** Blueprint-wide captured input fields, for a support-system-call action's own inputs. */
  readonly supportSystemFieldReferences: ReturnType<
    typeof import('../component-property-references.js').buildPropertyReferenceContext
  >['allFields'];
  announce(message: string): void;
  emitUpdated(serviceBlueprint: ServiceBlueprint, selection?: InspectorSelection | null): void;
}

export type InspectorHost = ReactiveControllerHost & HTMLElement & InspectorContext;
