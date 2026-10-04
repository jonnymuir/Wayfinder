import type { Component, ComponentDescriptor } from '../types.js';
import { collectStageInputFields } from '../component-property-references.js';
import { inScopeInputFieldKeys } from '../calculation-runtime.js';
import { type CalculationDiagnostic, computeCalculationDiagnostics } from '../calculation-diagnostics.js';
import { type DefinitionLint, type JsonObject, isJsonObject, lineOfString } from './lint-support.js';

type SeriesPart = 'from' | 'to' | 'values';
type SeriesPathPart = SeriesPart | 'over';
type NormalisedFields = Record<string, { expr?: string; source?: string }>;
type NormalisedSeries = Record<string, { over?: string; from?: string; to?: string; values?: Record<string, string> }>;

const asString = (value: unknown): string | undefined => (typeof value === 'string' ? value : undefined);

/** The object's entries as objects, ignoring anything that is not one. */
function objectMap(value: unknown): Record<string, JsonObject> {
  return isJsonObject(value) ? (value as Record<string, JsonObject>) : {};
}

function normaliseFields(fields: Record<string, JsonObject>): NormalisedFields {
  return Object.fromEntries(
    Object.entries(fields).map(([name, field]) => [name, { expr: asString(field.expr), source: asString(field.source) }])
  );
}

function normaliseSeries(series: Record<string, JsonObject>): NormalisedSeries {
  return Object.fromEntries(
    Object.entries(series).map(([name, definition]) => {
      const values = Object.entries(isJsonObject(definition.values) ? definition.values : {}).filter(
        (entry): entry is [string, string] => typeof entry[1] === 'string'
      );
      return [
        name,
        {
          over: asString(definition.over),
          from: asString(definition.from),
          to: asString(definition.to),
          values: Object.fromEntries(values),
        },
      ];
    })
  );
}

function stageInputFields(root: JsonObject, catalog: ComponentDescriptor[]) {
  const stages = Array.isArray(root.stages) ? root.stages : [];
  return stages.flatMap((stage) =>
    isJsonObject(stage) ? collectStageInputFields(stage.components as Component[] | undefined, catalog) : []
  );
}

/**
 * Mirrors the checks the Calculations tab and the Validation tab enforce, for anyone hand-editing
 * `calculations` as raw JSON in the Definition tab instead. Its only job is coercing raw,
 * possibly-malformed JSON into calculation-diagnostics.ts's structured input shape and turning its
 * result back into DefinitionLint messages with a source line — the actual checks live in
 * computeCalculationDiagnostics, shared with the Calculations tab and the Validation tab so none of
 * the three re-derives its own subset of the same rules.
 */
export function lintCalculations(root: JsonObject, source: string, catalog: ComponentDescriptor[], issues: DefinitionLint[]): void {
  if (!isJsonObject(root.calculations)) {
    return;
  }
  const calculations = root.calculations;

  const fields = normaliseFields(objectMap(calculations.fields));
  const series = normaliseSeries(objectMap(calculations.series));
  const diagnostics = computeCalculationDiagnostics({
    fields,
    series,
    tableNames: new Set(Object.keys(objectMap(calculations.tables))),
    inScopeInputFieldKeys: inScopeInputFieldKeys(stageInputFields(root, catalog)),
  });

  for (const diagnostic of diagnostics) {
    issues.push(describeDiagnostic(diagnostic, source, fields, series));
  }
}

const fieldPath = (field: string) => `calculations.fields.${field}`;
const seriesPath = (series: string, part: SeriesPathPart, column?: string) =>
  `calculations.series.${series}.${part}${column ? `.${column}` : ''}`;

function seriesExpression(series: NormalisedSeries, name: string, part: SeriesPart, column?: string): string {
  const definition = series[name];
  if (!definition) {
    return '';
  }
  return part === 'values' ? ((column && definition.values?.[column]) ?? '') : (definition[part] ?? '');
}

type Described = { message: string; pathHint: string; anchor?: string };
type Lookup = { fields: NormalisedFields; series: NormalisedSeries };
type Describe<K extends CalculationDiagnostic['kind']> = (
  diagnostic: Extract<CalculationDiagnostic, { kind: K }>,
  lookup: Lookup
) => Described;

const fieldExpression = (lookup: Lookup, field: string) => lookup.fields[field]?.expr ?? '';

/** One small description per diagnostic kind, so adding a kind is a compile error until it is described here. */
const DESCRIBE: { [K in CalculationDiagnostic['kind']]: Describe<K> } = {
  'field-parse-error': (d, l) => ({
    message: `"${fieldPath(d.field)}": ${d.message}`,
    pathHint: fieldPath(d.field),
    anchor: fieldExpression(l, d.field),
  }),
  'field-unknown-reference': (d, l) => ({
    message: `"${fieldPath(d.field)}" references "${d.name}", which is not a known input field or calculation field.`,
    pathHint: fieldPath(d.field),
    anchor: fieldExpression(l, d.field),
  }),
  'field-unknown-table': (d, l) => ({
    message: `"${fieldPath(d.field)}" calls lookup() against unknown table "${d.table}".`,
    pathHint: fieldPath(d.field),
    anchor: fieldExpression(l, d.field),
  }),
  'field-name-collision': (d) => ({
    message: `"${fieldPath(d.field)}" collides with an input field's own fieldKey ("${d.field}").`,
    pathHint: fieldPath(d.field),
    anchor: d.field,
  }),
  'field-cycle': (d) => ({
    message: `calculations.fields: circular dependency between ${d.fields.join(', ')} — these fields reference each other in a loop and can never be evaluated.`,
    pathHint: 'calculations.fields',
  }),
  'field-order': (d) => ({
    message: `calculations.fields are declared out of dependency order — "${d.field}" must be declared after "${d.mustFollow}". A field must be declared before anything that references it.`,
    pathHint: 'calculations.fields',
  }),
  'series-parse-error': (d, l) => ({
    message: `"${seriesPath(d.series, d.part, d.column)}": ${d.message}`,
    pathHint: seriesPath(d.series, d.part, d.column),
    anchor: seriesExpression(l.series, d.series, d.part, d.column),
  }),
  'series-unknown-reference': (d, l) => ({
    message: `"${seriesPath(d.series, d.part, d.column)}" references "${d.name}", which is not a known input field or calculation field.`,
    pathHint: seriesPath(d.series, d.part, d.column),
    anchor: seriesExpression(l.series, d.series, d.part, d.column),
  }),
  'series-unknown-table': (d, l) => ({
    message: `"${seriesPath(d.series, d.part, d.column)}" calls lookup() against unknown table "${d.table}".`,
    pathHint: seriesPath(d.series, d.part, d.column),
    anchor: seriesExpression(l.series, d.series, d.part, d.column),
  }),
  'series-loop-variable-collision': (d) => ({
    message: `"${seriesPath(d.series, 'over')}": loop variable "${d.variable}" collides with an existing field or input name.`,
    pathHint: seriesPath(d.series, 'over'),
    anchor: d.variable,
  }),
  'field-value-kind-without-service': (d) => ({
    message: `"${fieldPath(d.field)}" declares ${d.property}, which is only meaningful with "source": "service".`,
    pathHint: fieldPath(d.field),
    anchor: d.field,
  }),
  'field-invalid-value-kind': (d) => ({
    message: `"${fieldPath(d.field)}.valueKind": "${d.valueKind}" is not a valid valueKind — expected "number", "string" or "boolean", or omit it.`,
    pathHint: `${fieldPath(d.field)}.valueKind`,
    anchor: d.valueKind,
  }),
  'field-default-without-value-kind': (d) => ({
    message: `"${fieldPath(d.field)}" declares a default but no valueKind — validation can't parse the default without knowing its kind.`,
    pathHint: fieldPath(d.field),
    anchor: d.field,
  }),
  'field-default-unparseable': (d) => ({
    message: `"${fieldPath(d.field)}.default": not a valid ${d.valueKind} for the declared valueKind.`,
    pathHint: `${fieldPath(d.field)}.default`,
    anchor: d.field,
  }),
};

function describeDiagnostic(
  diagnostic: CalculationDiagnostic,
  source: string,
  fields: NormalisedFields,
  series: NormalisedSeries
): DefinitionLint {
  const describe = DESCRIBE[diagnostic.kind] as (diagnostic: CalculationDiagnostic, lookup: Lookup) => Described;
  const { message, pathHint, anchor } = describe(diagnostic, { fields, series });
  return anchor === undefined ? { message, pathHint } : { message, pathHint, line: lineOfString(source, anchor) };
}
