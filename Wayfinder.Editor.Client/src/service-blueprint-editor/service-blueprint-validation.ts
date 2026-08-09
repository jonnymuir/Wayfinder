import type { ActionCatalogEntry, AuthoredAction, AuthoredStage, AuthoredServiceBlueprint, ComponentDescriptor, RouteView } from './types.js';
import { stageActions, stageKind, serviceBlueprintGateways, serviceBlueprintStages } from './types.js';
import { findCatalogEntry, validateAction } from './action-editing.js';
import { flattenRoutes, outgoingRouteViews, inboundRouteViews } from './route-model.js';
import { collectStageInputFields } from './component-property-references.js';
import { inScopeInputFieldKeys } from './calculation-runtime.js';
import { computeCalculationDiagnostics, type CalculationDiagnostic } from './calculation-diagnostics.js';

const TERMINAL_STAGE_KINDS = new Set<AuthoredStage['metadata'] extends never ? never : ReturnType<typeof stageKind>>(['Confirmation']);

export type ServiceBlueprintValidationSeverity = 'error' | 'warning';

export type ServiceBlueprintValidationLocation =
  | { kind: 'stage'; stageKey: string }
  | { kind: 'route'; routeId: string }
  | {
      kind: 'action';
      target: 'stage' | 'route';
      stageKey?: string;
      routeId?: string;
      actionIndex: number;
      fieldKey?: string;
      formFieldIndex?: number;
    }
  | { kind: 'calculation'; field?: string; series?: string };

export interface ServiceBlueprintValidationIssue {
  id: string;
  code:
    | 'initial-stage-missing'
    | 'stage-orphaned'
    | 'stage-unreachable'
    | 'stage-dead-end'
    | 'route-missing-stage'
    | 'route-duplicate'
    | 'action-configuration'
    | 'calculation-parse-error'
    | 'calculation-unknown-reference'
    | 'calculation-unknown-table'
    | 'calculation-name-collision'
    | 'calculation-cycle'
    | 'calculation-order'
    | 'calculation-loop-variable-collision';
  severity: ServiceBlueprintValidationSeverity;
  message: string;
  blocking: boolean;
  location: ServiceBlueprintValidationLocation;
}

export function isTerminalStage(stage: AuthoredStage): boolean {
  return TERMINAL_STAGE_KINDS.has(stageKind(stage));
}

export function serviceBlueprintOutgoingRoutes(serviceBlueprint: AuthoredServiceBlueprint, stageKey: string): RouteView[] {
  return outgoingRouteViews(serviceBlueprint, stageKey);
}

export function serviceBlueprintInboundRoutes(serviceBlueprint: AuthoredServiceBlueprint, stageKey: string): RouteView[] {
  return inboundRouteViews(serviceBlueprint, stageKey);
}

export function serviceBlueprintReachableStageKeys(serviceBlueprint: AuthoredServiceBlueprint): Set<string> {
  const stageKeys = new Set(serviceBlueprintStages(serviceBlueprint).map(stage => stage.stateKey));
  const gatewayKeys = new Set(serviceBlueprintGateways(serviceBlueprint).map(gateway => gateway.key));
  if (stageKeys.size === 0) {
    return new Set<string>();
  }

  const startStageKey = stageKeys.has(serviceBlueprint.initialStage)
    ? serviceBlueprint.initialStage
    : serviceBlueprint.stages[0]?.stateKey;

  if (!startStageKey) {
    return new Set<string>();
  }

  const reachable = new Set<string>();
  const visitedNodes = new Set<string>();
  const pending = [startStageKey];
  const routes = flattenRoutes(serviceBlueprint);

  while (pending.length > 0) {
    const current = pending.shift();
    if (!current || visitedNodes.has(current)) {
      continue;
    }

    visitedNodes.add(current);

    if (stageKeys.has(current)) {
      reachable.add(current);
    }

    routes.forEach(route => {
      if (route.fromStage !== current) {
        return;
      }

      if ((stageKeys.has(route.toStage) || gatewayKeys.has(route.toStage)) && !visitedNodes.has(route.toStage)) {
        pending.push(route.toStage);
      }
    });
  }

  return reachable;
}

export function serviceBlueprintOrphanedStages(serviceBlueprint: AuthoredServiceBlueprint): AuthoredStage[] {
  return serviceBlueprint.stages.filter(stage =>
    stage.stateKey !== serviceBlueprint.initialStage
    && serviceBlueprintInboundRoutes(serviceBlueprint, stage.stateKey).length === 0
    && serviceBlueprintOutgoingRoutes(serviceBlueprint, stage.stateKey).length === 0
  );
}

export function serviceBlueprintUnreachableStages(serviceBlueprint: AuthoredServiceBlueprint): AuthoredStage[] {
  const reachable = serviceBlueprintReachableStageKeys(serviceBlueprint);
  const orphanedKeys = new Set(serviceBlueprintOrphanedStages(serviceBlueprint).map(stage => stage.stateKey));
  return serviceBlueprint.stages.filter(stage => !reachable.has(stage.stateKey) && !orphanedKeys.has(stage.stateKey));
}

export function serviceBlueprintDeadEndStages(serviceBlueprint: AuthoredServiceBlueprint): AuthoredStage[] {
  const orphanedKeys = new Set(serviceBlueprintOrphanedStages(serviceBlueprint).map(stage => stage.stateKey));
  return serviceBlueprint.stages.filter(stage =>
    !orphanedKeys.has(stage.stateKey)
    && !isTerminalStage(stage)
    && serviceBlueprintOutgoingRoutes(serviceBlueprint, stage.stateKey).length === 0
  );
}

export function serviceBlueprintRoutesWithMissingStages(serviceBlueprint: AuthoredServiceBlueprint): RouteView[] {
  const stageKeys = new Set(serviceBlueprint.stages.map(stage => stage.stateKey));
  const gatewayKeys = new Set(serviceBlueprintGateways(serviceBlueprint).map(gateway => gateway.key));
  return flattenRoutes(serviceBlueprint).filter(route =>
    (!stageKeys.has(route.fromStage) && !gatewayKeys.has(route.fromStage))
    || (!stageKeys.has(route.toStage) && !gatewayKeys.has(route.toStage))
  );
}

function stageLabel(serviceBlueprint: AuthoredServiceBlueprint, stageKey: string) {
  return serviceBlueprint.stages.find(stage => stage.stateKey === stageKey)?.displayName
    ?? serviceBlueprintGateways(serviceBlueprint).find(gateway => gateway.key === stageKey)?.displayName
    ?? stageKey;
}

function actionLabel(entry: ActionCatalogEntry | null, action: AuthoredAction) {
  return entry?.label ?? action.summary?.trim() ?? action.type;
}

function routeLabel(serviceBlueprint: AuthoredServiceBlueprint, view: RouteView) {
  return `${stageLabel(serviceBlueprint, view.fromStage)} → ${stageLabel(serviceBlueprint, view.toStage)}`;
}

function normaliseValidationMessage(message: string) {
  return message.endsWith('.') ? message : `${message}.`;
}

function actionValidationIssues(
  serviceBlueprint: AuthoredServiceBlueprint,
  actionCatalog: ActionCatalogEntry[],
  action: AuthoredAction,
  location: Extract<ServiceBlueprintValidationLocation, { kind: 'action' }>,
  routeView?: RouteView
): ServiceBlueprintValidationIssue[] {
  const entry = findCatalogEntry(actionCatalog, action.type);
  const validation = validateAction(entry, action);
  const baseLabel = actionLabel(entry, action);
  const parentLabel = location.target === 'stage'
    ? stageLabel(serviceBlueprint, location.stageKey ?? '')
    : routeView
      ? routeLabel(serviceBlueprint, routeView)
      : location.routeId ?? '';

  const propertyIssues = Object.entries(validation.propertyErrors).map(([fieldKey, message]) => ({
    id: `${location.target}-${location.stageKey ?? location.routeId}-action-${location.actionIndex}-${fieldKey}`,
    code: 'action-configuration' as const,
    severity: 'warning' as const,
    blocking: false,
    location: { ...location, fieldKey },
    message: location.target === 'stage'
      ? `Stage “${parentLabel}” has an action that needs attention: “${baseLabel}” — ${normaliseValidationMessage(message)}`
      : `Route “${parentLabel}” has an action that needs attention: “${baseLabel}” — ${normaliseValidationMessage(message)}`,
  }));

  const formFieldIssues = Object.entries(validation.formFieldErrors).flatMap(([fieldIndex, fieldErrors]) =>
    Object.entries(fieldErrors).flatMap(([fieldKey, message]) => {
      if (!message) {
        return [];
      }

      return [{
        id: `${location.target}-${location.stageKey ?? location.routeId}-action-${location.actionIndex}-form-${fieldIndex}-${fieldKey}`,
        code: 'action-configuration' as const,
        severity: 'warning' as const,
        blocking: false,
        location: {
          ...location,
          fieldKey: 'fields',
          formFieldIndex: Number(fieldIndex),
        },
        message: location.target === 'stage'
          ? `Stage “${parentLabel}” has a form action that needs attention: “${baseLabel}” — ${normaliseValidationMessage(message)}`
          : `Route “${parentLabel}” has a form action that needs attention: “${baseLabel}” — ${normaliseValidationMessage(message)}`,
      }];
    })
  );

  return [...propertyIssues, ...formFieldIssues];
}

/**
 * Every diagnostic computeCalculationDiagnostics produces corresponds to something
 * CalculationEvaluator.cs/CalculationScopeBuilder.cs genuinely rejects at Save time (see
 * calculation-diagnostics.ts's own doc comment), so every one is reported here as a blocking
 * error — proactively surfacing, before Save is even attempted, exactly what SaveAsync's own
 * server-side Validate() call would otherwise reject it for. Shared with the Definition tab's
 * lint (service-blueprint-lint.ts) and the Calculations tab (wayfinder-calculations-editor.ts)
 * so none of the three re-derives its own subset of the same rules.
 */
function calculationValidationIssues(
  serviceBlueprint: AuthoredServiceBlueprint,
  componentCatalog: ComponentDescriptor[]
): ServiceBlueprintValidationIssue[] {
  const calculations = serviceBlueprint.calculations;
  if (!calculations) {
    return [];
  }

  const allComponents = serviceBlueprint.stages.flatMap(stage => stage.components ?? []);
  const allInputFields = collectStageInputFields(allComponents, componentCatalog);
  const scopedInputFieldKeys = inScopeInputFieldKeys(allInputFields);

  const diagnostics = computeCalculationDiagnostics({
    fields: calculations.fields,
    series: calculations.series ?? {},
    tableNames: new Set(Object.keys(calculations.tables ?? {})),
    inScopeInputFieldKeys: scopedInputFieldKeys,
  });

  return diagnostics.map((diagnostic, index) => calculationDiagnosticToIssue(diagnostic, index));
}

function calculationDiagnosticToIssue(diagnostic: CalculationDiagnostic, index: number): ServiceBlueprintValidationIssue {
  const base = { severity: 'error' as const, blocking: true };

  switch (diagnostic.kind) {
    case 'field-parse-error':
      return {
        ...base,
        id: `calculation-field-parse-error-${diagnostic.field}`,
        code: 'calculation-parse-error',
        location: { kind: 'calculation', field: diagnostic.field },
        message: `Calculation field “${diagnostic.field}” has an invalid expression: ${diagnostic.message}`,
      };
    case 'field-unknown-reference':
      return {
        ...base,
        id: `calculation-field-unknown-reference-${diagnostic.field}-${diagnostic.name}`,
        code: 'calculation-unknown-reference',
        location: { kind: 'calculation', field: diagnostic.field },
        message: `Calculation field “${diagnostic.field}” references “${diagnostic.name}”, which is not a known input field or calculation field.`,
      };
    case 'field-unknown-table':
      return {
        ...base,
        id: `calculation-field-unknown-table-${diagnostic.field}-${diagnostic.table}`,
        code: 'calculation-unknown-table',
        location: { kind: 'calculation', field: diagnostic.field },
        message: `Calculation field “${diagnostic.field}” calls lookup() against unknown table “${diagnostic.table}”.`,
      };
    case 'field-name-collision':
      return {
        ...base,
        id: `calculation-field-name-collision-${diagnostic.field}`,
        code: 'calculation-name-collision',
        location: { kind: 'calculation', field: diagnostic.field },
        message: `Calculation field “${diagnostic.field}” collides with an input field's own fieldKey.`,
      };
    case 'field-cycle':
      return {
        ...base,
        id: `calculation-field-cycle-${diagnostic.fields.join('-')}`,
        code: 'calculation-cycle',
        location: { kind: 'calculation' },
        message: `Calculation fields ${diagnostic.fields.join(' → ')} → ${diagnostic.fields[0]} form a circular dependency and can never be evaluated.`,
      };
    case 'field-order':
      return {
        ...base,
        id: `calculation-field-order-${diagnostic.field}`,
        code: 'calculation-order',
        location: { kind: 'calculation', field: diagnostic.field },
        message: `Calculation field “${diagnostic.field}” must be declared after “${diagnostic.mustFollow}” — a field must be declared before anything that references it.`,
      };
    case 'series-parse-error':
      return {
        ...base,
        id: `calculation-series-parse-error-${diagnostic.series}-${diagnostic.part}-${diagnostic.column ?? index}`,
        code: 'calculation-parse-error',
        location: { kind: 'calculation', series: diagnostic.series },
        message: `Calculation series “${diagnostic.series}” (${diagnostic.column ?? diagnostic.part}) has an invalid expression: ${diagnostic.message}`,
      };
    case 'series-unknown-reference':
      return {
        ...base,
        id: `calculation-series-unknown-reference-${diagnostic.series}-${diagnostic.part}-${diagnostic.column ?? index}-${diagnostic.name}`,
        code: 'calculation-unknown-reference',
        location: { kind: 'calculation', series: diagnostic.series },
        message: `Calculation series “${diagnostic.series}” (${diagnostic.column ?? diagnostic.part}) references “${diagnostic.name}”, which is not a known input field or calculation field.`,
      };
    case 'series-unknown-table':
      return {
        ...base,
        id: `calculation-series-unknown-table-${diagnostic.series}-${diagnostic.part}-${diagnostic.column ?? index}-${diagnostic.table}`,
        code: 'calculation-unknown-table',
        location: { kind: 'calculation', series: diagnostic.series },
        message: `Calculation series “${diagnostic.series}” (${diagnostic.column ?? diagnostic.part}) calls lookup() against unknown table “${diagnostic.table}”.`,
      };
    case 'series-loop-variable-collision':
      return {
        ...base,
        id: `calculation-series-loop-variable-collision-${diagnostic.series}`,
        code: 'calculation-loop-variable-collision',
        location: { kind: 'calculation', series: diagnostic.series },
        message: `Calculation series “${diagnostic.series}”'s loop variable “${diagnostic.variable}” collides with an existing field or input name.`,
      };
  }
}

export function validateServiceBlueprint(
  serviceBlueprint: AuthoredServiceBlueprint,
  actionCatalog: ActionCatalogEntry[] = [],
  componentCatalog: ComponentDescriptor[] = []
): ServiceBlueprintValidationIssue[] {
  const initialStageExists = serviceBlueprint.stages.some(stage => stage.stateKey === serviceBlueprint.initialStage);
  const initialStageIssues = initialStageExists || serviceBlueprint.stages.length === 0
    ? []
    : [{
        id: 'initial-stage-missing',
        code: 'initial-stage-missing' as const,
        severity: 'error' as const,
        blocking: true,
        location: { kind: 'stage', stageKey: serviceBlueprint.initialStage || serviceBlueprint.stages[0]?.stateKey || '' } as const,
        message: serviceBlueprint.initialStage
          ? `The service blueprint start stage “${serviceBlueprint.initialStage}” is missing. Pick an existing initial stage before you save or simulate this service blueprint.`
          : 'The service blueprint does not have an initial stage yet. Pick one before you save or simulate this service blueprint.',
      }];

  const orphanedIssues = serviceBlueprintOrphanedStages(serviceBlueprint).map(stage => ({
    id: `stage-orphaned-${stage.stateKey}`,
    code: 'stage-orphaned' as const,
    severity: 'error' as const,
    blocking: true,
    location: { kind: 'stage', stageKey: stage.stateKey } as const,
    message: `Stage “${stage.displayName}” is orphaned. Connect it through a gateway so authors can reach it.`,
  }));

  const unreachableIssues = serviceBlueprintUnreachableStages(serviceBlueprint).map(stage => ({
    id: `stage-unreachable-${stage.stateKey}`,
    code: 'stage-unreachable' as const,
    severity: 'error' as const,
    blocking: true,
    location: { kind: 'stage', stageKey: stage.stateKey } as const,
    message: `Stage “${stage.displayName}” is unreachable from the service blueprint start. Add or retarget a route through a gateway so authors can get there.`,
  }));

  const deadEndIssues = serviceBlueprintDeadEndStages(serviceBlueprint).map(stage => ({
    id: `stage-dead-end-${stage.stateKey}`,
    code: 'stage-dead-end' as const,
    severity: 'warning' as const,
    blocking: false,
    location: { kind: 'stage', stageKey: stage.stateKey } as const,
    message: `Stage “${stage.displayName}” has no outgoing route through a gateway yet.`,
  }));

  const duplicateRouteKeys = new Set<string>();
  const duplicateRouteIssues = flattenRoutes(serviceBlueprint).flatMap(view => {
    const key = `${view.fromStage}::${view.action}::${view.toStage}`;
    if (duplicateRouteKeys.has(key)) {
      return [{
        id: `route-duplicate-${view.routeId}`,
        code: 'route-duplicate' as const,
        severity: 'error' as const,
        blocking: true,
        location: { kind: 'route', routeId: view.routeId } as const,
        message: `Route “${view.action}” from “${stageLabel(serviceBlueprint, view.fromStage)}” to “${stageLabel(serviceBlueprint, view.toStage)}” is duplicated. Keep each route unique in the flat contract.`,
      }];
    }

    duplicateRouteKeys.add(key);
    return [];
  });

  const missingStageRouteIssues = serviceBlueprintRoutesWithMissingStages(serviceBlueprint).map(view => {
    const stageKeys = new Set(serviceBlueprint.stages.map(stage => stage.stateKey));
    const gatewayKeys = new Set(serviceBlueprintGateways(serviceBlueprint).map(gateway => gateway.key));
    const missingSource = !stageKeys.has(view.fromStage) && !gatewayKeys.has(view.fromStage);
    const missingTarget = !stageKeys.has(view.toStage) && !gatewayKeys.has(view.toStage);
    const missingLabel = missingTarget ? view.toStage : view.fromStage;
    const direction = missingTarget ? 'target' : 'source';

    return {
      id: `route-missing-stage-${view.routeId}`,
      code: 'route-missing-stage' as const,
      severity: 'error' as const,
      blocking: true,
      location: { kind: 'route', routeId: view.routeId } as const,
      message: missingSource && missingTarget
        ? `Route “${view.action}” is disconnected because both ends are missing. Reconnect it to existing stages before you save or simulate this service blueprint.`
        : `Route “${view.action}” points to a missing ${direction} step “${missingLabel}”. Reconnect it to an existing stage or gateway before you save or simulate this service blueprint.`,
    };
  });

  const stageActionIssues = serviceBlueprint.stages.flatMap(stage =>
    stageActions(stage).flatMap((action, actionIndex) =>
      actionValidationIssues(serviceBlueprint, actionCatalog, action, {
        kind: 'action',
        target: 'stage',
        stageKey: stage.stateKey,
        actionIndex,
      })
    )
  );

  const routeActionIssues = flattenRoutes(serviceBlueprint).flatMap(view =>
    (view.actions ?? []).flatMap((action, actionIndex) =>
      actionValidationIssues(serviceBlueprint, actionCatalog, action, {
        kind: 'action',
        target: 'route',
        routeId: view.routeId,
        actionIndex,
      }, view)
    )
  );

  return [
    ...initialStageIssues,
    ...orphanedIssues,
    ...unreachableIssues,
    ...deadEndIssues,
    ...duplicateRouteIssues,
    ...missingStageRouteIssues,
    ...stageActionIssues,
    ...routeActionIssues,
    ...calculationValidationIssues(serviceBlueprint, componentCatalog),
  ];
}
