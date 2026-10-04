import type { ServiceBlueprintCalculationSet, ServiceBlueprintStageValidationRule, StageDefinition } from '../types.js';
import { type FieldInput, computeStableFieldOrder } from '../calculation-ordering.js';

/**
 * What each edit in the Calculations tab does to the `calculations` block (and to a stage's
 * validation rules). Pure: each takes the current value and returns the next, so the rules live
 * apart from the sections that render the controls.
 */

export type CalcFields = ServiceBlueprintCalculationSet['fields'];
export type CalcFieldDefinition = CalcFields[string];
export type CalcTables = NonNullable<ServiceBlueprintCalculationSet['tables']>;
export type CalcTableDefinition = CalcTables[string];
export type CalcSeriesMap = NonNullable<ServiceBlueprintCalculationSet['series']>;
export type CalcSeriesDefinition = CalcSeriesMap[string];

// ── name-keyed records ──────────────────────────────────────────────────────

/** `prefix1`, `prefix2`, … — the first that is not already a key. */
export function uniqueName(prefix: string, existing: Record<string, unknown>): string {
  let suffix = 1;
  while (`${prefix}${suffix}` in existing) {
    suffix += 1;
  }
  return `${prefix}${suffix}`;
}

/** The record with `from` renamed to `to`, keeping every entry where it was. */
export function renameKey<T>(record: Record<string, T>, from: string, to: string): Record<string, T> {
  return Object.fromEntries(Object.entries(record).map(([key, value]) => [key === from ? to : key, value]));
}

export function omitKey<T>(record: Record<string, T>, key: string): Record<string, T> {
  const next = { ...record };
  delete next[key];
  return next;
}

/** A new name worth applying: trimmed, non-empty, and different from the current one. */
export function changedName(current: string, typed: string): string | null {
  const trimmed = typed.trim();
  return trimmed && trimmed !== current ? trimmed : null;
}

// ── fields ──────────────────────────────────────────────────────────────────

export const fieldEdits = {
  add(fields: CalcFields): { fields: CalcFields; name: string } {
    const name = uniqueName('field', fields);
    return { fields: { ...fields, [name]: { expr: '' } }, name };
  },

  setExpr: (fields: CalcFields, name: string, expr: string): CalcFields => ({ ...fields, [name]: { ...fields[name], expr } }),

  setFormat(fields: CalcFields, name: string, format: string): CalcFields {
    const current = fields[name] ?? {};
    return { ...fields, [name]: format ? { ...current, format } : { ...current, format: undefined } };
  },

  /**
   * Switches a field between an authored expression and a value the host supplies. Keeps any
   * hand-authored valueKind/default (validation-only aids — see ServiceBlueprintCalculationField)
   * rather than dropping them on the toggle; the Calculations tab has no field for them yet, but
   * the Definition tab and server validator both read them.
   */
  setSource(fields: CalcFields, name: string, isService: boolean): CalcFields {
    if (!isService) {
      return { ...fields, [name]: { expr: '' } };
    }
    const { valueKind, default: defaultValue } = fields[name] ?? {};
    return {
      ...fields,
      [name]: { source: 'service', ...(valueKind ? { valueKind } : {}), ...(defaultValue ? { default: defaultValue } : {}) },
    };
  },
};

/** What is wrong with a field's name, or null. */
export function fieldNameError(name: string, order: string[], collidingFieldNames: Set<string>): string | null {
  if (!name.trim()) return 'Name is required.';
  if (collidingFieldNames.has(name)) return `Collides with an input field's own fieldKey ("${name}").`;
  return order.filter((existing) => existing === name).length > 1 ? 'Duplicate field name.' : null;
}

/**
 * Puts `fields` in dependency order (calculation-ordering.ts) so the persisted declaration order is
 * always valid without the designer thinking about it, and says which fields moved. A genuine cycle
 * has no valid order, so the fields are kept as typed — the cycle banner and the Validation tab both
 * derive the same cycle from the persisted fields, so there is nothing further to record.
 */
export function inDependencyOrder(fields: CalcFields, currentOrder: string[]): { fields: CalcFields; announcement: string | null } {
  const inputs: FieldInput[] = Object.entries(fields).map(([name, field]) => ({ name, expr: field.expr ?? '' }));
  const result = computeStableFieldOrder(inputs, currentOrder);
  if (!result.ok) {
    return { fields, announcement: null };
  }

  const announcement =
    result.moved.length > 0
      ? result.moved.map((move) => `Moved "${move.name}" after "${move.movedAfter}" because it now depends on it.`).join(' ')
      : null;
  return { fields: Object.fromEntries(result.order.map((name) => [name, fields[name]])), announcement };
}

// ── tables ──────────────────────────────────────────────────────────────────

export const tableEdits = {
  add(tables: CalcTables): { tables: CalcTables; name: string } {
    const name = uniqueName('table', tables);
    return { tables: { ...tables, [name]: { interpolate: 'linear', values: {} } }, name };
  },

  setInterpolate: (tables: CalcTables, name: string, interpolate: string): CalcTables => ({
    ...tables,
    [name]: { ...tables[name], interpolate },
  }),

  /** A new row, keyed with the first unused whole number from 0. */
  addRow(tables: CalcTables, name: string): CalcTables {
    const table = tables[name] ?? { values: {} };
    let suffix = 0;
    while (String(suffix) in table.values) {
      suffix += 1;
    }
    return { ...tables, [name]: { ...table, values: { ...table.values, [String(suffix)]: 0 } } };
  },

  deleteRow: (tables: CalcTables, name: string, key: string): CalcTables =>
    tables[name] ? { ...tables, [name]: { ...tables[name], values: omitKey(tables[name].values, key) } } : tables,

  renameRow: (tables: CalcTables, name: string, from: string, to: string): CalcTables =>
    tables[name] ? { ...tables, [name]: { ...tables[name], values: renameKey(tables[name].values, from, to) } } : tables,

  setRowValue: (tables: CalcTables, name: string, key: string, value: number): CalcTables =>
    tables[name] ? { ...tables, [name]: { ...tables[name], values: { ...tables[name].values, [key]: value } } } : tables,
};

// ── series ──────────────────────────────────────────────────────────────────

const BLANK_SERIES: CalcSeriesDefinition = { over: '', from: '', to: '', values: {} };

export const seriesEdits = {
  add(series: CalcSeriesMap): { series: CalcSeriesMap; name: string } {
    const name = uniqueName('series', series);
    return { series: { ...series, [name]: { over: 'i', from: '1', to: '1', values: {} } }, name };
  },

  setField<K extends keyof CalcSeriesDefinition>(
    series: CalcSeriesMap,
    name: string,
    key: K,
    value: CalcSeriesDefinition[K]
  ): CalcSeriesMap {
    return { ...series, [name]: { ...(series[name] ?? BLANK_SERIES), [key]: value } };
  },

  addColumn(series: CalcSeriesMap, name: string): CalcSeriesMap {
    const current = series[name] ?? BLANK_SERIES;
    return { ...series, [name]: { ...current, values: { ...current.values, [uniqueName('column', current.values)]: '' } } };
  },

  deleteColumn: (series: CalcSeriesMap, name: string, column: string): CalcSeriesMap =>
    series[name] ? { ...series, [name]: { ...series[name], values: omitKey(series[name].values, column) } } : series,

  renameColumn: (series: CalcSeriesMap, name: string, from: string, to: string): CalcSeriesMap =>
    series[name] ? { ...series, [name]: { ...series[name], values: renameKey(series[name].values, from, to) } } : series,

  setColumnExpr: (series: CalcSeriesMap, name: string, column: string, expr: string): CalcSeriesMap =>
    series[name] ? { ...series, [name]: { ...series[name], values: { ...series[name].values, [column]: expr } } } : series,
};

// ── stage validation rules ──────────────────────────────────────────────────

export const validationEdits = {
  /** Starts empty, like a new field: an incomplete row is expected to be transiently invalid while being authored. */
  add: (stage: StageDefinition): ServiceBlueprintStageValidationRule[] => [
    ...(stage.validations ?? []),
    { code: '', rule: '', message: '' },
  ],

  remove: (stage: StageDefinition, index: number): ServiceBlueprintStageValidationRule[] =>
    (stage.validations ?? []).filter((_, i) => i !== index),

  patch: (
    stage: StageDefinition,
    index: number,
    patch: Partial<ServiceBlueprintStageValidationRule>
  ): ServiceBlueprintStageValidationRule[] => (stage.validations ?? []).map((rule, i) => (i === index ? { ...rule, ...patch } : rule)),
};
