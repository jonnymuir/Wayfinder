/**
 * The editor's view of a service blueprint. The model itself is generated from the C# model
 * (`generated/wayfinder-model.ts`, see Wayfinder.TypeGen) and re-exported here; this file adds
 * only what the editor alone owns.
 */

export type {
  AccordionComponent,
  ActionTiming,
  AccordionSection,
  ActionDefinition,
  BodyComponent,
  BooleanComponent,
  BulkDataReviewComponent,
  ChartBand,
  ChartComponent,
  CheckboxesComponent,
  Component,
  ComponentCategory,
  ComponentContainment,
  ComponentDescriptor,
  ComponentPropertyDescriptor,
  ComponentPropertyValueKind,
  ContainmentKind,
  DateInputComponent,
  DecimalInputComponent,
  DetailsComponent,
  EmailComponent,
  FieldsetComponent,
  FileUploadComponent,
  GatewayKind,
  GuidanceChecklistComponent,
  GuidanceChecklistItem,
  HandoffDefinition,
  HeadingComponent,
  InsetTextComponent,
  NodePosition,
  NotificationBannerComponent,
  NumberInputComponent,
  PanelComponent,
  QueueDefinition,
  RadiosComponent,
  SelectComponent,
  ServiceBlueprint,
  ServiceBlueprintCalculationField,
  ServiceBlueprintCalculationFieldShape,
  ServiceBlueprintCalculationSeries,
  ServiceBlueprintCalculationSet,
  ServiceBlueprintCalculationTable,
  ServiceBlueprintDiagnostic,
  ServiceBlueprintDiagnosticSeverity,
  ServiceBlueprintGatewayDefinition,
  ServiceBlueprintLayoutDefinition,
  ServiceBlueprintRouteDefinition,
  ServiceBlueprintSaveOutcome,
  ServiceBlueprintSaveStatus,
  ServiceBlueprintStageValidationRule,
  ServiceBlueprintValidationOutcome,
  SliderComponent,
  StageDefinition,
  StageKind,
  StatGroupComponent,
  StatItemDefinition,
  SummaryListComponent,
  SupportSystemCapabilityDescriptor,
  SupportSystemCompletionMode,
  SupportSystemDescriptor,
  SupportSystemOutcomeDescriptor,
  TaskItem,
  TaskListComponent,
  TaskSection,
  TextInputComponent,
  TextareaComponent,
  WaitingComponent,
  WarningTextComponent,
} from './generated/wayfinder-model.js';

export { gatewayKindValues, stageKindValues } from './generated/wayfinder-model.js';

import type {
  ActionDefinition,
  ServiceBlueprint,
  ServiceBlueprintGatewayDefinition,
  ServiceBlueprintRouteDefinition,
  QueueDefinition,
  StageDefinition,
} from './generated/wayfinder-model.js';

export function serviceBlueprintStages(serviceBlueprint: Pick<ServiceBlueprint, 'stages'> | null | undefined): StageDefinition[] {
  return serviceBlueprint?.stages ?? [];
}

export function serviceBlueprintGateways(
  serviceBlueprint: Pick<ServiceBlueprint, 'gateways'> | null | undefined
): ServiceBlueprintGatewayDefinition[] {
  return serviceBlueprint?.gateways ?? [];
}

export function serviceBlueprintQueues(serviceBlueprint: Pick<ServiceBlueprint, 'queues'> | null | undefined): QueueDefinition[] {
  return serviceBlueprint?.queues ?? [];
}

// ---------------------------------------------------------------------------
// Route view
// ---------------------------------------------------------------------------
//
// Editor surfaces render routes as a flattened view, derived from each stage's and
// gateway's own routes.

/**
 * Read-only flattening of a stage's or gateway's route into a route/editor view.
 */
export interface RouteView {
  fromStage: string;
  toStage: string;
  action: string;
  actions?: ActionDefinition[];
  requiresRole?: string;
  showWhen?: string;
  fromGateway?: string;
  toGateway?: string;
  gatewayKey?: string;
  key?: string;
  routeIndex: number;
  routeId: string;
}

export type { ServiceBlueprintRouteDefinition as RouteDefinition };

// ---------------------------------------------------------------------------
// Action catalog (what the action editor offers; not part of the blueprint model)
// ---------------------------------------------------------------------------

export type ParameterValueKind = 'String' | 'Number' | 'Integer' | 'Boolean' | 'Object' | 'Array' | 'Null';

export interface AuthoredParameterDefinition {
  key: string;
  title: string;
  description?: string;
  valueKind: ParameterValueKind;
  format?: string;
  editor?: string;
  allowedValues?: string[];
  defaultValue?: unknown;
  properties?: AuthoredParameterDefinition[];
  items?: AuthoredParameterDefinition | null;
}

export interface AuthoredParameterSchema {
  key: string;
  title: string;
  description?: string;
  appliesTo?: string[];
  valueKind?: ParameterValueKind;
  allowAdditionalProperties?: boolean;
  properties?: AuthoredParameterDefinition[];
  required?: string[];
}

export interface ActionCatalogEntry {
  type: string;
  label: string;
  summary: string;
  appliesTo: string[];
  paramsSchema: AuthoredParameterSchema;
  parameterWidgets?: Record<string, string>;
  defaultParams?: Record<string, unknown>;
  status?: string;
  runtimeImplementation?: string;
}

/**
 * The well-known `params` shape of a `support-system-call` action (see
 * Wayfinder/Models/ServiceDesign/SupportSystems/SupportSystemDescriptor.cs's
 * `SupportSystemActionTypes`) — `inputs` maps a capability's declared input `key` to the
 * blueprint field key it's sourced from. This is a shape convention read/written through
 * `ActionDefinition.params`, not a distinct wire type of its own.
 */
export interface SupportSystemCallActionParams {
  supportSystemKey?: string;
  capabilityKey?: string;
  inputs?: Record<string, string>;
}

export type ActionFormFieldType = 'text' | 'number' | 'textarea' | 'select' | 'radio' | 'date';

export interface ActionFormFieldConfig {
  fieldKey: string;
  label: string;
  type: ActionFormFieldType;
  required: boolean;
  hintText?: string;
  validationPattern?: string;
  defaultValue?: string;
  options: string[];
}
