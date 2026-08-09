import type { AuthoredComponent, AuthoredServiceBlueprint, ComponentDescriptor, ComponentPropertyDescriptor } from './types.js';
import { hydrateServiceBlueprintDefinition } from './types.js';
import { collectStageInputFields } from './component-property-references.js';
import { inScopeInputFieldKeys } from './calculation-runtime.js';
import { computeCalculationDiagnostics, type CalculationDiagnostic } from './calculation-diagnostics.js';

export type DefinitionLint = {
  message: string;
  line?: number;
  pathHint?: string;
};

const ALLOWED_STAGE_KINDS = new Set(['Question', 'CheckAnswers', 'Confirmation', 'TaskList']);
const ALLOWED_GATEWAY_KINDS = new Set(['Split', 'Join']);

/**
 * Blueprint-wide reference data for the three dangling-reference checks below — mirrors what
 * `ServiceBlueprint.ValidateFieldReferences` (Wayfinder/Models/ServiceDesign/ServiceBlueprint.cs)
 * checks server-side, so a mistake made by hand in the Definition tab is flagged before you even
 * try to save, the same class of feedback `validate_service_blueprint` gives, just local and
 * instant. `siblingFieldKeys` is stage-scoped (ConditionalOn/VisibleWhen are only ever checked
 * against the current stage's own submitted values); `calculationFieldNames`/`stageKeys` are
 * blueprint-wide.
 */
interface ReferenceLintContext {
  siblingFieldKeys: Set<string>;
  calculationFieldNames: Set<string>;
  stageKeys: Set<string>;
}

function findLine(source: string, needle: string): number | undefined {
  const index = source.indexOf(needle);
  if (index < 0) {
    return undefined;
  }
  return source.slice(0, index).split('\n').length;
}

/**
 * Phase 7 — live, as-you-type component validation in the Definition tab, mirroring
 * Wayfinder.Engine.Services.ComponentPropertyValidator's own checks (required/allowedValues/
 * pattern/length/numeric constraints, plus a KeyedChildren key not matching its sibling Options)
 * against the same live ComponentDescriptor catalog the properties-panel add/edit UI (phase 6)
 * already fetches — so a mistake made by hand in the JSON editor is flagged before you even try
 * to save, the same class of feedback validate_service_blueprint gives, just local and instant.
 * Recurses into a container's own children the same way component-child-editor.ts (phase 6b)
 * does, so a nested component gets checked too, not just top-level ones.
 */
function lintComponentTree(
  components: unknown,
  catalog: ComponentDescriptor[],
  source: string,
  pathPrefix: string,
  issues: DefinitionLint[],
  refs?: ReferenceLintContext
): void {
  if (!Array.isArray(components)) {
    return;
  }

  components.forEach((rawComponent, index) => {
    if (!rawComponent || typeof rawComponent !== 'object' || Array.isArray(rawComponent)) {
      issues.push({ message: `Component at "${pathPrefix}[${index}]" must be an object.`, pathHint: `${pathPrefix}[${index}]` });
      return;
    }

    const component = rawComponent as Record<string, unknown>;
    const componentPath = `${pathPrefix}[${index}]`;
    const discriminator = typeof component.type === 'string' ? component.type : '';
    const descriptor = catalog.find(candidate => candidate.discriminator === discriminator);

    if (!discriminator) {
      issues.push({ message: `Component at "${componentPath}" is missing "type".`, pathHint: componentPath });
      return;
    }

    if (!descriptor) {
      issues.push({
        message: `Unknown component type "${discriminator}" at "${componentPath}". Call list_component_types (MCP) or GET /component-types to see every registered discriminator.`,
        pathHint: componentPath,
        line: findLine(source, `"${discriminator}"`),
      });
      return;
    }

    lintComponentProperties(component, descriptor.properties, source, componentPath, issues, refs);

    const { containment } = descriptor;
    if (containment.kind === 'ChildList' && containment.propertyName) {
      const key = containment.propertyName;
      lintComponentTree(component[key], catalog, source, `${componentPath}.${key}`, issues, refs);
    } else if (containment.kind === 'NamedSections' && containment.propertyName) {
      const key = containment.propertyName;
      const childrenKey = containment.sectionChildrenPropertyName ?? 'children';
      const sections = component[key];
      if (Array.isArray(sections)) {
        sections.forEach((rawSection, sectionIndex) => {
          if (rawSection && typeof rawSection === 'object' && !Array.isArray(rawSection)) {
            const section = rawSection as Record<string, unknown>;
            lintComponentTree(section[childrenKey], catalog, source, `${componentPath}.${key}[${sectionIndex}].${childrenKey}`, issues, refs);
          }
        });
      }
    } else if (containment.kind === 'KeyedChildren' && containment.propertyName && containment.keySourceProperty) {
      const key = containment.propertyName;
      const optionsKey = containment.keySourceProperty;
      const byKey = component[key];
      const options = Array.isArray(component[optionsKey]) ? (component[optionsKey] as unknown[]).map(String) : [];
      if (byKey && typeof byKey === 'object' && !Array.isArray(byKey)) {
        for (const [optionKey, children] of Object.entries(byKey as Record<string, unknown>)) {
          if (!options.includes(optionKey)) {
            issues.push({
              message: `"${optionKey}" is a key in "${componentPath}.${key}" but not one of the values declared in "${componentPath}.${optionsKey}" — this branch can never be shown.`,
              pathHint: `${componentPath}.${key}.${optionKey}`,
              line: findLine(source, `"${optionKey}"`),
            });
          }
          lintComponentTree(children, catalog, source, `${componentPath}.${key}.${optionKey}`, issues, refs);
        }
      }
    }
  });
}

function lintComponentProperties(
  component: Record<string, unknown>,
  properties: ComponentPropertyDescriptor[],
  source: string,
  path: string,
  issues: DefinitionLint[],
  refs?: ReferenceLintContext
): void {
  for (const property of properties) {
    const value = component[property.key];
    const propertyPath = `${path}.${property.key}`;
    const isMissing = value === undefined || value === null
      || (typeof value === 'string' && value.trim() === '')
      || (Array.isArray(value) && value.length === 0);

    if (isMissing) {
      if (property.required) {
        issues.push({ message: `"${property.title}" is required at "${propertyPath}" but is missing or empty.`, pathHint: propertyPath });
      }
      continue;
    }

    if (typeof value === 'string') {
      if (property.allowedValues?.length && !property.allowedValues.includes(value)) {
        issues.push({
          message: `"${property.title}" at "${propertyPath}" is "${value}", which isn't one of: ${property.allowedValues.join(', ')}.`,
          pathHint: propertyPath,
          line: findLine(source, `"${value}"`),
        });
      }
      // Dangling-reference checks — mirrors ServiceBlueprint.ValidateFieldReferences/
      // ValidateDataDisplayBindings server-side, so a mistake made by hand here is caught before
      // Save, not just downstream at validate_service_blueprint.
      if (refs) {
        if (property.format === 'field-ref' && !refs.siblingFieldKeys.has(value)) {
          issues.push({
            message: `"${property.title}" at "${propertyPath}" is "${value}", which isn't another field's fieldKey in this stage — visibility is only ever checked against the current stage's own submitted values, so this field would always stay hidden.`,
            pathHint: propertyPath,
            line: findLine(source, `"${value}"`),
          });
        }
        if (property.format === 'calculation-ref' && !refs.calculationFieldNames.has(value)) {
          issues.push({
            message: `"${property.title}" at "${propertyPath}" is "${value}", which is not a name declared in this blueprint's calculations.fields — it would never resolve.`,
            pathHint: propertyPath,
            line: findLine(source, `"${value}"`),
          });
        }
        if (property.format === 'stage-ref' && !refs.stageKeys.has(value)) {
          issues.push({
            message: `"${property.title}" at "${propertyPath}" is "${value}", which is not a stage in this blueprint.`,
            pathHint: propertyPath,
            line: findLine(source, `"${value}"`),
          });
        }
      }
      if (property.pattern) {
        try {
          if (!new RegExp(property.pattern).test(value)) {
            issues.push({ message: `"${property.title}" at "${propertyPath}" does not match the required pattern.`, pathHint: propertyPath });
          }
        } catch {
          // An invalid regex is a descriptor-authoring bug, not something this document can fix.
        }
      }
      if (property.minLength != null && value.length < property.minLength) {
        issues.push({ message: `"${property.title}" at "${propertyPath}" must be at least ${property.minLength} character(s) long.`, pathHint: propertyPath });
      }
      if (property.maxLength != null && value.length > property.maxLength) {
        issues.push({ message: `"${property.title}" at "${propertyPath}" must be at most ${property.maxLength} character(s) long.`, pathHint: propertyPath });
      }
    } else if (typeof value === 'number') {
      if (property.minimum != null && value < property.minimum) {
        issues.push({ message: `"${property.title}" at "${propertyPath}" must be at least ${property.minimum}.`, pathHint: propertyPath });
      }
      if (property.maximum != null && value > property.maximum) {
        issues.push({ message: `"${property.title}" at "${propertyPath}" must be at most ${property.maximum}.`, pathHint: propertyPath });
      }
    }

    if (property.valueKind === 'Array' && Array.isArray(value) && property.items?.properties) {
      value.forEach((item, itemIndex) => {
        if (item && typeof item === 'object' && !Array.isArray(item)) {
          lintComponentProperties(item as Record<string, unknown>, property.items!.properties!, source, `${propertyPath}[${itemIndex}]`, issues, refs);
        }
      });
    } else if (property.valueKind === 'Object' && value && typeof value === 'object' && !Array.isArray(value) && property.properties) {
      lintComponentProperties(value as Record<string, unknown>, property.properties, source, propertyPath, issues, refs);
    }
  }
}

/**
 * Mirrors the checks the Calculations tab and the Validation tab enforce, for anyone hand-editing
 * `calculations` as raw JSON in the Definition tab instead: a field/series expression that
 * doesn't parse, an identifier that isn't a known input field/earlier field/table, a field or
 * series loop-variable name that collides with an input's own fieldKey, and a `fields`
 * declaration order that isn't already valid (forward references or a genuine cycle) — the same
 * "field must be declared before it's referenced" rule docs/guides/calculation-language.md
 * documents. This function's only job is coercing raw, possibly-malformed JSON into
 * calculation-diagnostics.ts's structured input shape and turning its result back into
 * DefinitionLint messages with a source line — the actual checks live in
 * computeCalculationDiagnostics, shared with the Calculations tab and the Validation tab so none
 * of the three re-derives its own subset of the same rules.
 */
function lintCalculations(
  root: Record<string, unknown>,
  source: string,
  componentCatalog: ComponentDescriptor[],
  issues: DefinitionLint[]
): void {
  if (!root.calculations || typeof root.calculations !== 'object' || Array.isArray(root.calculations)) {
    return;
  }
  const calculations = root.calculations as Record<string, unknown>;

  const tableNames = new Set(
    calculations.tables && typeof calculations.tables === 'object' && !Array.isArray(calculations.tables)
      ? Object.keys(calculations.tables as Record<string, unknown>)
      : []
  );

  const allInputFields = Array.isArray(root.stages)
    ? root.stages.flatMap(rawState => {
        if (!rawState || typeof rawState !== 'object' || Array.isArray(rawState)) {
          return [];
        }
        const components = (rawState as Record<string, unknown>).components;
        return collectStageInputFields(components as AuthoredComponent[] | undefined, componentCatalog);
      })
    : [];
  const scopedInputFieldKeys = inScopeInputFieldKeys(allInputFields);

  const fields =
    calculations.fields && typeof calculations.fields === 'object' && !Array.isArray(calculations.fields)
      ? (calculations.fields as Record<string, Record<string, unknown>>)
      : {};
  const series =
    calculations.series && typeof calculations.series === 'object' && !Array.isArray(calculations.series)
      ? (calculations.series as Record<string, Record<string, unknown>>)
      : {};

  const normalisedFields: Record<string, { expr?: string; source?: string }> = {};
  for (const [name, field] of Object.entries(fields)) {
    normalisedFields[name] = {
      expr: typeof field.expr === 'string' ? field.expr : undefined,
      source: typeof field.source === 'string' ? field.source : undefined,
    };
  }

  const normalisedSeries: Record<string, { over?: string; from?: string; to?: string; values?: Record<string, string> }> = {};
  for (const [name, definition] of Object.entries(series)) {
    const values =
      definition.values && typeof definition.values === 'object' && !Array.isArray(definition.values)
        ? (definition.values as Record<string, unknown>)
        : {};
    const normalisedValues: Record<string, string> = {};
    for (const [column, expr] of Object.entries(values)) {
      if (typeof expr === 'string') {
        normalisedValues[column] = expr;
      }
    }
    normalisedSeries[name] = {
      over: typeof definition.over === 'string' ? definition.over : undefined,
      from: typeof definition.from === 'string' ? definition.from : undefined,
      to: typeof definition.to === 'string' ? definition.to : undefined,
      values: normalisedValues,
    };
  }

  const diagnostics = computeCalculationDiagnostics({
    fields: normalisedFields,
    series: normalisedSeries,
    tableNames,
    inScopeInputFieldKeys: scopedInputFieldKeys,
  });

  for (const diagnostic of diagnostics) {
    issues.push(diagnosticToDefinitionLint(diagnostic, source, normalisedFields, normalisedSeries));
  }
}

function diagnosticToDefinitionLint(
  diagnostic: CalculationDiagnostic,
  source: string,
  fields: Record<string, { expr?: string; source?: string }>,
  series: Record<string, { over?: string; from?: string; to?: string; values?: Record<string, string> }>
): DefinitionLint {
  switch (diagnostic.kind) {
    case 'field-parse-error': {
      const pathHint = `calculations.fields.${diagnostic.field}`;
      const expr = fields[diagnostic.field]?.expr ?? '';
      return { message: `"${pathHint}": ${diagnostic.message}`, pathHint, line: findLine(source, `"${expr}"`) };
    }
    case 'field-unknown-reference': {
      const pathHint = `calculations.fields.${diagnostic.field}`;
      const expr = fields[diagnostic.field]?.expr ?? '';
      return {
        message: `"${pathHint}" references "${diagnostic.name}", which is not a known input field or calculation field.`,
        pathHint,
        line: findLine(source, `"${expr}"`),
      };
    }
    case 'field-unknown-table': {
      const pathHint = `calculations.fields.${diagnostic.field}`;
      const expr = fields[diagnostic.field]?.expr ?? '';
      return {
        message: `"${pathHint}" calls lookup() against unknown table "${diagnostic.table}".`,
        pathHint,
        line: findLine(source, `"${expr}"`),
      };
    }
    case 'field-name-collision': {
      const pathHint = `calculations.fields.${diagnostic.field}`;
      return {
        message: `"${pathHint}" collides with an input field's own fieldKey ("${diagnostic.field}").`,
        pathHint,
        line: findLine(source, `"${diagnostic.field}"`),
      };
    }
    case 'field-cycle':
      return {
        message: `calculations.fields: circular dependency between ${diagnostic.fields.join(', ')} — these fields reference each other in a loop and can never be evaluated.`,
        pathHint: 'calculations.fields',
      };
    case 'field-order':
      return {
        message: `calculations.fields are declared out of dependency order — "${diagnostic.field}" must be declared after "${diagnostic.mustFollow}". A field must be declared before anything that references it.`,
        pathHint: 'calculations.fields',
      };
    case 'series-parse-error': {
      const pathHint = `calculations.series.${diagnostic.series}.${diagnostic.part}${diagnostic.column ? `.${diagnostic.column}` : ''}`;
      const expr = seriesExpr(series, diagnostic.series, diagnostic.part, diagnostic.column);
      return { message: `"${pathHint}": ${diagnostic.message}`, pathHint, line: findLine(source, `"${expr}"`) };
    }
    case 'series-unknown-reference': {
      const pathHint = `calculations.series.${diagnostic.series}.${diagnostic.part}${diagnostic.column ? `.${diagnostic.column}` : ''}`;
      const expr = seriesExpr(series, diagnostic.series, diagnostic.part, diagnostic.column);
      return {
        message: `"${pathHint}" references "${diagnostic.name}", which is not a known input field or calculation field.`,
        pathHint,
        line: findLine(source, `"${expr}"`),
      };
    }
    case 'series-unknown-table': {
      const pathHint = `calculations.series.${diagnostic.series}.${diagnostic.part}${diagnostic.column ? `.${diagnostic.column}` : ''}`;
      const expr = seriesExpr(series, diagnostic.series, diagnostic.part, diagnostic.column);
      return {
        message: `"${pathHint}" calls lookup() against unknown table "${diagnostic.table}".`,
        pathHint,
        line: findLine(source, `"${expr}"`),
      };
    }
    case 'series-loop-variable-collision': {
      const pathHint = `calculations.series.${diagnostic.series}.over`;
      return {
        message: `"${pathHint}": loop variable "${diagnostic.variable}" collides with an existing field or input name.`,
        pathHint,
        line: findLine(source, `"${diagnostic.variable}"`),
      };
    }
  }
}

function seriesExpr(
  series: Record<string, { over?: string; from?: string; to?: string; values?: Record<string, string> }>,
  name: string,
  part: 'from' | 'to' | 'values',
  column?: string
): string {
  const definition = series[name];
  if (!definition) {
    return '';
  }
  if (part === 'values') {
    return (column && definition.values?.[column]) ?? '';
  }
  return definition[part] ?? '';
}

export function lintAuthoredServiceBlueprintDocument(
  parsed: unknown,
  source: string,
  componentCatalog: ComponentDescriptor[] = []
): DefinitionLint[] {
  const issues: DefinitionLint[] = [];

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    issues.push({ message: 'Definition must be a JSON object.' });
    return issues;
  }

  const root = parsed as Record<string, unknown>;

  for (const required of ['definitionKey', 'displayName', 'initialStage']) {
    if (typeof root[required] !== 'string' || !(root[required] as string).trim()) {
      issues.push({
        message: `Missing or empty "${required}".`,
        pathHint: required,
        line: findLine(source, `"${required}"`),
      });
    }
  }

  if (!Array.isArray(root.queues)) {
    issues.push({ message: '"queues" must be an array.', pathHint: 'queues' });
  }

  lintCalculations(root, source, componentCatalog, issues);

  const calculationFieldNames = new Set(
    root.calculations && typeof root.calculations === 'object'
      ? Object.keys((root.calculations as Record<string, unknown>).fields ?? {})
      : []
  );
  const stageKeys = new Set(
    Array.isArray(root.stages)
      ? root.stages
          .map(rawState => {
            if (!rawState || typeof rawState !== 'object' || Array.isArray(rawState)) {
              return undefined;
            }
            const state = rawState as Record<string, unknown>;
            return typeof state.stageKey === 'string' ? state.stageKey : undefined;
          })
          .filter((key): key is string => Boolean(key))
      : []
  );

  if (!Array.isArray(root.stages)) {
    issues.push({ message: '"stages" must be an array.', pathHint: 'stages' });
  } else {
    const seenStateKeys = new Set<string>();
    root.stages.forEach((rawState, index) => {
      if (!rawState || typeof rawState !== 'object' || Array.isArray(rawState)) {
        issues.push({ message: `State at index ${index} must be an object.` });
        return;
      }

      const state = rawState as Record<string, unknown>;
      const stateKey = typeof state.stageKey === 'string'
        ? state.stageKey
        : typeof state.stateKey === 'string'
          ? state.stateKey
          : '';
      if (!stateKey.trim()) {
        issues.push({ message: `State at index ${index} is missing "stageKey".` });
      } else if (seenStateKeys.has(stateKey)) {
        issues.push({
          message: `Duplicate stage key "${stateKey}".`,
          line: findLine(source, `"${stateKey}"`),
        });
      } else {
        seenStateKeys.add(stateKey);
      }

      const kind = typeof state.stageType === 'string' && state.stageType
        ? state.stageType
        : typeof state.stageType === 'string' && state.stageType
          ? state.stageType
          : typeof (state.metadata as Record<string, unknown> | undefined)?.stageType === 'string'
            ? String((state.metadata as Record<string, unknown>).stageType)
            : typeof (state.metadata as Record<string, unknown> | undefined)?.stageType === 'string'
              ? String((state.metadata as Record<string, unknown>).stageType)
              : '';
      if (kind && !ALLOWED_STAGE_KINDS.has(kind)) {
        issues.push({
          message: `State "${stateKey || index}" has unsupported stageType "${kind}". Allowed kinds: ${[...ALLOWED_STAGE_KINDS].join(', ')}.`,
          line: findLine(source, `"${kind}"`),
        });
      }

      if (typeof state.queueKey !== 'string' || !state.queueKey.trim()) {
        issues.push({ message: `State "${stateKey || index}" is missing "queueKey".` });
      }

      if (state.routes !== undefined && !Array.isArray(state.routes)) {
        issues.push({ message: `State "${stateKey || index}" has a non-array "routes" value.` });
      }

      if (componentCatalog.length > 0 && state.components !== undefined) {
        const siblingFieldKeys = new Set(
          collectStageInputFields(state.components as AuthoredComponent[], componentCatalog).map(field => field.fieldKey)
        );
        lintComponentTree(state.components, componentCatalog, source, `stages[${index}].components`, issues, {
          siblingFieldKeys,
          calculationFieldNames,
          stageKeys,
        });
      }
    });
  }

  if (!Array.isArray(root.gateways)) {
    issues.push({ message: '"gateways" must be an array.', pathHint: 'gateways' });
  } else {
    const seenGatewayKeys = new Set<string>();
    root.gateways.forEach((rawGateway, index) => {
      if (!rawGateway || typeof rawGateway !== 'object' || Array.isArray(rawGateway)) {
        issues.push({ message: `Gateway at index ${index} must be an object.` });
        return;
      }

      const gateway = rawGateway as Record<string, unknown>;
      const key = typeof gateway.key === 'string' ? gateway.key : '';
      if (!key.trim()) {
        issues.push({ message: `Gateway at index ${index} is missing "key".` });
      } else if (seenGatewayKeys.has(key)) {
        issues.push({
          message: `Duplicate gateway key "${key}".`,
          line: findLine(source, `"${key}"`),
        });
      } else {
        seenGatewayKeys.add(key);
      }

      const kind = typeof gateway.gatewayType === 'string' ? gateway.gatewayType : '';
      if (kind && !ALLOWED_GATEWAY_KINDS.has(kind)) {
        issues.push({
          message: `Gateway "${key || index}" has unsupported gatewayType "${kind}". Allowed kinds: ${[...ALLOWED_GATEWAY_KINDS].join(', ')}.`,
          line: findLine(source, `"${kind}"`),
        });
      }

      if (typeof gateway.queueKey !== 'string' || !gateway.queueKey.trim()) {
        issues.push({ message: `Gateway "${key || index}" is missing "queueKey".` });
      }

      if (!Array.isArray(gateway.routes)) {
        issues.push({ message: `Gateway "${key || index}" must declare a "routes" array.` });
      }
    });
  }

  return issues;
}

export function coerceParsedAuthoredServiceBlueprint(parsed: unknown): AuthoredServiceBlueprint {
  const root = parsed as Record<string, unknown>;
  return hydrateServiceBlueprintDefinition({
    definitionKey: String(root.definitionKey ?? ''),
    displayName: String(root.displayName ?? ''),
    version: typeof root.version === 'number' ? root.version : 1,
    initialStage: String(root.initialStage ?? ''),
    requestPolicy: String(root.requestPolicy ?? 'single'),
    description: typeof root.description === 'string' ? root.description : undefined,
    schemaVersion: typeof root.schemaVersion === 'string' ? root.schemaVersion : undefined,
    queues: Array.isArray(root.queues) ? (root.queues as AuthoredServiceBlueprint['queues']) : [],
    stages: Array.isArray(root.stages) ? (root.stages as AuthoredServiceBlueprint['stages']) : [],
    gateways: Array.isArray(root.gateways) ? (root.gateways as AuthoredServiceBlueprint['gateways']) : [],
    calculations: root.calculations
      ? (root.calculations as AuthoredServiceBlueprint['calculations'])
      : undefined,
    parameterSchemas: Array.isArray(root.parameterSchemas)
      ? (root.parameterSchemas as AuthoredServiceBlueprint['parameterSchemas'])
      : undefined,
    layout: root.layout ? (root.layout as AuthoredServiceBlueprint['layout']) : undefined,
  });
}
