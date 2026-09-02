/**
 * The single place that computes "what's wrong with this calculations block" — every rule here
 * mirrors something Wayfinder/Services/Calculations/CalculationEvaluator.cs (or
 * CalculationScopeBuilder.cs) genuinely rejects at Save time, so every diagnostic is safe to treat
 * as blocking wherever it's surfaced. Three consumers share this instead of each re-deriving their
 * own subset of the same rules: service-blueprint-lint.ts (Definition tab, raw hand-edited JSON),
 * service-blueprint-validation.ts (Validation tab, gates the Save button), and
 * wayfinder-calculations-editor.ts (Calculations tab's field-name-collision and cycle checks —
 * its expression parse errors and series loop-variable collisions already have better, live
 * mechanisms: the CodeMirror linter and tryEvaluateSeriesForPreview's own scope-based check,
 * respectively, so it doesn't need every diagnostic kind this module produces).
 *
 * Reuses calculation-runtime.ts (the real grammar, via tryParseExpression/extractReferencedNames)
 * and calculation-ordering.ts (the same stable topological sort the Calculations tab uses to
 * auto-reorder fields) — no new expression-parsing or ordering logic lives here, just the
 * orchestration of what to check and how to report it.
 */

import { extractReferencedNames, tryParseExpression } from './calculation-runtime.js';
import { computeStableFieldOrder, type FieldInput } from './calculation-ordering.js';

export interface CalculationDiagnosticsInput {
  fields: Record<string, { expr?: string; source?: string; valueKind?: string; default?: string }>;
  series: Record<string, { over?: string; from?: string; to?: string; values?: Record<string, string> }>;
  tableNames: Set<string>;
  /**
   * Only input fieldKeys CalculationScopeBuilder.Build can statically guarantee are in the calc
   * scope. A numeric input (slider/number/decimal) with neither a submission nor a declared
   * default is genuinely absent from scope, not an error — there's no safe placeholder for a
   * missing amount. Every other field type always resolves (an unfilled text box already means ""
   * and an unticked checkbox already means false) even with no default — see
   * docs/guides/calculation-language.md. Never pass every input's fieldKey here; use
   * calculation-runtime.ts's `inScopeInputFieldKeys` to build this.
   */
  inScopeInputFieldKeys: Set<string>;
}

export type CalculationDiagnostic =
  | { kind: 'field-parse-error'; field: string; message: string }
  | { kind: 'field-unknown-reference'; field: string; name: string }
  | { kind: 'field-unknown-table'; field: string; table: string }
  | { kind: 'field-name-collision'; field: string }
  | { kind: 'field-cycle'; fields: string[] }
  | { kind: 'field-order'; field: string; mustFollow: string }
  | { kind: 'series-parse-error'; series: string; part: 'from' | 'to' | 'values'; column?: string; message: string }
  | { kind: 'series-unknown-reference'; series: string; part: 'from' | 'to' | 'values'; column?: string; name: string }
  | { kind: 'series-unknown-table'; series: string; part: 'from' | 'to' | 'values'; column?: string; table: string }
  | { kind: 'series-loop-variable-collision'; series: string; variable: string }
  | { kind: 'field-value-kind-without-service'; field: string; property: 'valueKind' | 'default' }
  | { kind: 'field-invalid-value-kind'; field: string; valueKind: string }
  | { kind: 'field-default-without-value-kind'; field: string }
  | { kind: 'field-default-unparseable'; field: string; valueKind: string };

const SCALAR_VALUE_KINDS = new Set(['number', 'string', 'boolean']);

export function computeCalculationDiagnostics(input: CalculationDiagnosticsInput): CalculationDiagnostic[] {
  const diagnostics: CalculationDiagnostic[] = [];
  const fieldNames = Object.keys(input.fields);
  const fieldNameSet = new Set(fieldNames);

  function checkExpression(
    expr: string,
    onParseError: (message: string) => void,
    onUnknownReference: (name: string) => void,
    onUnknownTable: (table: string) => void,
    /** A series' own loop variable — valid inside its `values` columns (evaluated against a
     * rowScope that already has it added, see CalculationEvaluator.cs's EvaluateSeries), but NOT
     * inside `from`/`to` (evaluated before the loop variable exists). Omitted for fields. */
    additionalKnownName?: string
  ): void {
    const parseResult = tryParseExpression(expr);
    if (!parseResult.ok) {
      onParseError(parseResult.message);
      return;
    }

    const referenced = extractReferencedNames(expr);
    if (!referenced) {
      return;
    }
    for (const name of referenced.scopeNames) {
      if (name !== additionalKnownName && !fieldNameSet.has(name) && !input.inScopeInputFieldKeys.has(name)) {
        onUnknownReference(name);
      }
    }
    for (const table of referenced.tableNames) {
      if (!input.tableNames.has(table)) {
        onUnknownTable(table);
      }
    }
  }

  const fieldInputs: FieldInput[] = [];
  for (const [name, field] of Object.entries(input.fields)) {
    const isService = (field.source ?? '').toLowerCase() === 'service';
    const expr = field.expr ?? '';
    fieldInputs.push({ name, expr });

    if (input.inScopeInputFieldKeys.has(name)) {
      diagnostics.push({ kind: 'field-name-collision', field: name });
    }

    // valueKind/default: only meaningful with source: "service" (mirrors ServiceBlueprint.cs's
    // ValidateDataDisplayBindings). "" default is a real, intentional value, so trim-check only.
    const hasValueKind = typeof field.valueKind === 'string' && field.valueKind.trim() !== '';
    const hasDefault = typeof field.default === 'string' && field.default.trim() !== '';
    const normalisedKind = hasValueKind ? field.valueKind!.trim().toLowerCase() : '';
    if (!isService && hasValueKind) {
      diagnostics.push({ kind: 'field-value-kind-without-service', field: name, property: 'valueKind' });
    } else if (!isService && hasDefault) {
      diagnostics.push({ kind: 'field-value-kind-without-service', field: name, property: 'default' });
    } else if (isService) {
      if (hasValueKind && !SCALAR_VALUE_KINDS.has(normalisedKind)) {
        diagnostics.push({ kind: 'field-invalid-value-kind', field: name, valueKind: field.valueKind!.trim() });
      }
      if (hasDefault && !hasValueKind) {
        diagnostics.push({ kind: 'field-default-without-value-kind', field: name });
      } else if (hasDefault && normalisedKind === 'number'
        && !Number.isFinite(Number(field.default!.replace(/[£,]/g, '').trim()))) {
        diagnostics.push({ kind: 'field-default-unparseable', field: name, valueKind: 'number' });
      } else if (hasDefault && normalisedKind === 'boolean'
        && !['true', 'false'].includes(field.default!.trim().toLowerCase())) {
        diagnostics.push({ kind: 'field-default-unparseable', field: name, valueKind: 'boolean' });
      }
    }

    if (!isService && expr.trim()) {
      checkExpression(
        expr,
        message => diagnostics.push({ kind: 'field-parse-error', field: name, message }),
        refName => diagnostics.push({ kind: 'field-unknown-reference', field: name, name: refName }),
        table => diagnostics.push({ kind: 'field-unknown-table', field: name, table })
      );
    }
  }

  const orderResult = computeStableFieldOrder(fieldInputs, fieldNames);
  if (!orderResult.ok) {
    diagnostics.push({ kind: 'field-cycle', fields: orderResult.cycle });
  } else if (orderResult.moved.length > 0) {
    // One summary diagnostic naming the first violation, not one per move — fields are declared
    // before referenced or they aren't; the Calculations tab fixes every move automatically on
    // the next edit, so this is "something needs re-ordering," not a per-field checklist.
    const [firstMove] = orderResult.moved;
    diagnostics.push({ kind: 'field-order', field: firstMove.name, mustFollow: firstMove.movedAfter });
  }

  for (const [name, definition] of Object.entries(input.series)) {
    const over = definition.over ?? '';
    if (over && (fieldNameSet.has(over) || input.inScopeInputFieldKeys.has(over))) {
      diagnostics.push({ kind: 'series-loop-variable-collision', series: name, variable: over });
    }

    const from = definition.from ?? '';
    if (from.trim()) {
      checkExpression(
        from,
        message => diagnostics.push({ kind: 'series-parse-error', series: name, part: 'from', message }),
        refName => diagnostics.push({ kind: 'series-unknown-reference', series: name, part: 'from', name: refName }),
        table => diagnostics.push({ kind: 'series-unknown-table', series: name, part: 'from', table })
      );
    }

    const to = definition.to ?? '';
    if (to.trim()) {
      checkExpression(
        to,
        message => diagnostics.push({ kind: 'series-parse-error', series: name, part: 'to', message }),
        refName => diagnostics.push({ kind: 'series-unknown-reference', series: name, part: 'to', name: refName }),
        table => diagnostics.push({ kind: 'series-unknown-table', series: name, part: 'to', table })
      );
    }

    for (const [column, expr] of Object.entries(definition.values ?? {})) {
      if (typeof expr === 'string' && expr.trim()) {
        checkExpression(
          expr,
          message => diagnostics.push({ kind: 'series-parse-error', series: name, part: 'values', column, message }),
          refName => diagnostics.push({ kind: 'series-unknown-reference', series: name, part: 'values', column, name: refName }),
          table => diagnostics.push({ kind: 'series-unknown-table', series: name, part: 'values', column, table }),
          over
        );
      }
    }
  }

  return diagnostics;
}
