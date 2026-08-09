/**
 * Typed wrapper around the real calculation-language runtime — reused directly, not
 * reimplemented (see docs/guides/calculation-language.md and the "verified technical facts"
 * this was grounded against). This is the ONLY module that imports the raw `.js` file; every
 * other module in this editor (the Calculations tab, the CodeMirror expression editor, the
 * Definition-tab lint checks, the field-ordering algorithm) goes through this one instead.
 *
 * `wayfinder-calculations.js` has no "collect every failure" evaluator (unlike C#'s
 * `CalculationEvaluator.EvaluateCollectingErrors`) — `evaluateCalculations` throws on the FIRST
 * failure, which would make a live preview all-or-nothing (one unresolvable service field blanks
 * every other field's result, even ones with no relation to it). `tryEvaluateFieldsForPreview`
 * instead orchestrates the real per-expression evaluator (`evaluateExpression`) itself, one field
 * at a time, so a field's own failure — or an upstream service field with no supplied value —
 * only ever affects that field and whatever genuinely depends on it, not the whole set.
 */

import {
  CalculationError,
  Dec,
  evaluateExpression,
  parseExpression,
  toScope,
  type CalculationNode,
  type CalculationSet,
  type CalculationSeriesDefinition,
} from '../../../Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-calculations.js';

export type { CalculationNode, CalculationSet, CalculationSeriesDefinition };

/** Mirrors the real (non-exported) MAX_SERIES_ROWS in wayfinder-calculations.js — a defensive
 * cap for this module's own hand-rolled series preview loop, not the real engine's own check. */
const MAX_PREVIEW_SERIES_ROWS = 1000;

export type ParseResult =
  | { ok: true; node: CalculationNode }
  | { ok: false; message: string; position?: number };

/** Syntax-only check — no scope needed, instant feedback. */
export function tryParseExpression(expression: string): ParseResult {
  try {
    return { ok: true, node: parseExpression(expression) };
  } catch (error) {
    return { ok: false, ...describeError(error) };
  }
}

export interface ReferencedNames {
  /** Bare identifiers resolved as normal scope values — inputs, earlier fields, service fields, a series loop variable. */
  scopeNames: string[];
  /** lookup()'s first argument specifically — a table reference, never a scope value (see calculation-language.md's "Tables and lookup()"). */
  tableNames: string[];
}

/** Parses `expression` and walks its AST for referenced names. `null` if it doesn't parse — the
 * caller (calculation-ordering.ts, the "insert a reference" UI) should treat that as "nothing
 * determinable yet", not an error of its own; `tryParseExpression` is what surfaces the syntax
 * error itself. */
export function extractReferencedNames(expression: string): ReferencedNames | null {
  let node: CalculationNode;
  try {
    node = parseExpression(expression);
  } catch {
    return null;
  }

  const scopeNames = new Set<string>();
  const tableNames = new Set<string>();
  walk(node, scopeNames, tableNames);
  return { scopeNames: [...scopeNames], tableNames: [...tableNames] };
}

function walk(node: CalculationNode, scopeNames: Set<string>, tableNames: Set<string>): void {
  switch (node.kind) {
    case 'number':
    case 'text':
    case 'bool':
      return;
    case 'identifier':
      scopeNames.add(firstSegment(node.path));
      return;
    case 'unary':
      walk(node.operand, scopeNames, tableNames);
      return;
    case 'binary':
      walk(node.left, scopeNames, tableNames);
      walk(node.right, scopeNames, tableNames);
      return;
    case 'call':
      if (node.name === 'lookup' && node.args.length > 0 && node.args[0].kind === 'identifier') {
        tableNames.add(node.args[0].path);
        for (const arg of node.args.slice(1)) {
          walk(arg, scopeNames, tableNames);
        }
        return;
      }
      for (const arg of node.args) {
        walk(arg, scopeNames, tableNames);
      }
      return;
  }
}

function firstSegment(path: string): string {
  const dot = path.indexOf('.');
  return dot === -1 ? path : path.slice(0, dot);
}

export type FieldPreviewResult =
  | { status: 'ok'; display: string }
  | { status: 'error'; message: string }
  | { status: 'service' };

/**
 * Evaluates every non-service field in `calculations.fields`, in the object's own key order —
 * the caller is responsible for that order already being a valid topological order (see
 * calculation-ordering.ts); this function trusts it rather than re-deriving it, so there's one
 * source of truth for "what order do fields evaluate in."
 *
 * Returns the final scope alongside the per-field display results — a series preview needs to
 * evaluate against the same resolved fields, and re-deriving that scope a second time would mean
 * two different code paths for "what a field resolved to," which could drift.
 */
export function tryEvaluateFieldsForPreview(
  calculations: CalculationSet,
  sampleInputs: Record<string, unknown>
): { results: Record<string, FieldPreviewResult>; scope: Record<string, unknown> } {
  const scope: Record<string, unknown> = toScope(sampleInputs);
  const results: Record<string, FieldPreviewResult> = {};

  for (const [name, field] of Object.entries(calculations.fields ?? {})) {
    if ((field.source ?? '').toLowerCase() === 'service') {
      results[name] = { status: 'service' };
      continue;
    }

    if (!field.expr || !field.expr.trim()) {
      results[name] = { status: 'error', message: 'No expression set.' };
      continue;
    }

    try {
      const value = evaluateExpression(field.expr, scope, calculations);
      scope[name] = value;
      results[name] = { status: 'ok', display: displayValue(value) };
    } catch (error) {
      results[name] = { status: 'error', message: describeError(error).message };
    }
  }

  return { results, scope };
}

export type SeriesPreviewResult =
  | { status: 'ok'; rows: Array<Record<string, string>> }
  | { status: 'error'; message: string };

/** Evaluates one series against a scope already populated by `tryEvaluateFieldsForPreview`
 * (fields must be resolved first — a series may reference them). */
export function tryEvaluateSeriesForPreview(
  definition: CalculationSeriesDefinition,
  scope: Record<string, unknown>,
  calculations: CalculationSet
): SeriesPreviewResult {
  try {
    if (!definition.over || !definition.over.trim()) {
      return { status: 'error', message: "Series has no loop variable ('over')." };
    }

    if (definition.over in scope) {
      return { status: 'error', message: `Loop variable '${definition.over}' collides with an existing name.` };
    }

    const from = toIntegerBound(evaluateExpression(definition.from, scope, calculations), "'from'");
    const to = toIntegerBound(evaluateExpression(definition.to, scope, calculations), "'to'");

    if (to - from + 1 > MAX_PREVIEW_SERIES_ROWS) {
      return {
        status: 'error',
        message: `Would produce ${to - from + 1} rows; the limit is ${MAX_PREVIEW_SERIES_ROWS}.`,
      };
    }

    const rows: Array<Record<string, string>> = [];
    for (let step = from; step <= to; step++) {
      const rowScope = { ...scope, [definition.over]: Dec.fromNumber(step) };
      const row: Record<string, string> = { [definition.over]: String(step) };
      for (const [column, expr] of Object.entries(definition.values ?? {})) {
        row[column] = displayValue(evaluateExpression(expr, rowScope, calculations));
      }
      rows.push(row);
    }

    return { status: 'ok', rows };
  } catch (error) {
    return { status: 'error', message: describeError(error).message };
  }
}

function toIntegerBound(value: unknown, label: string): number {
  const n = value instanceof Dec ? value.toNumber() : Number(value);
  if (!Number.isFinite(n) || !Number.isInteger(n)) {
    throw new CalculationError(`${label} must resolve to a whole number.`);
  }
  return n;
}

function displayValue(value: unknown): string {
  if (value instanceof Dec) {
    return value.toString();
  }
  return String(value);
}

function describeError(error: unknown): { message: string; position?: number } {
  if (error instanceof CalculationError) {
    const match = /at position (\d+)/.exec(error.message);
    return { message: error.message, position: match ? Number(match[1]) : undefined };
  }
  return { message: error instanceof Error ? error.message : String(error) };
}
