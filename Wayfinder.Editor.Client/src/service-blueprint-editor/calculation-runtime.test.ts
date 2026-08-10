import {
  extractReferencedNames,
  inScopeInputFieldKeys,
  tryEvaluateFieldsForPreview,
  tryEvaluateSeriesForPreview,
  tryParseExpression,
  type CalculationSet,
} from './calculation-runtime.js';
import type { FieldReference } from './component-property-references.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

export function run(): number {
  failures = 0;

  // ── tryParseExpression ───────────────────────────────────────────────────
  {
    const result = tryParseExpression('1 + 2 * 3');
    check('a valid expression parses ok', result.ok === true, String(result.ok));
  }

  {
    const result = tryParseExpression('1 + ');
    check('an invalid expression reports ok:false with a message', result.ok === false, JSON.stringify(result));
  }

  // ── inScopeInputFieldKeys ──────────────────────────────────────────────────
  {
    const fields: FieldReference[] = [
      { fieldKey: 'withDefault', label: 'With default', type: 'text', default: 'hello' },
      // Regression: a declared default of "" is still a real default — CalculationScopeBuilder.cs
      // only excludes a field from scope when it has NEITHER a submission NOR a default; an empty
      // string is a legitimate default, not "no default". A plain truthy check on field.default
      // (`field.default` alone) would wrongly treat this as absent, producing a false
      // "unknown reference" for a correctly-defaulted textarea/text input — exactly the false
      // positive real juggling-licence.json's riskMitigationNotes field (default: "") hit live.
      { fieldKey: 'withEmptyStringDefault', label: 'With empty default', type: 'textarea', default: '' },
      { fieldKey: 'noDefault', label: 'No default', type: 'text' },
    ];
    const inScope = inScopeInputFieldKeys(fields);
    check('a field with a non-empty declared default is in scope', inScope.has('withDefault'));
    check('a field with a declared EMPTY STRING default is still in scope', inScope.has('withEmptyStringDefault'));
    check('a field with no declared default is not in scope', !inScope.has('noDefault'));
  }

  // ── extractReferencedNames ────────────────────────────────────────────────
  {
    const result = extractReferencedNames('a + b * 2');
    check('scope names from a simple binary expression', JSON.stringify(result?.scopeNames.sort()) === JSON.stringify(['a', 'b']), JSON.stringify(result));
    check('no table names when there is no lookup() call', result?.tableNames.length === 0, JSON.stringify(result));
  }

  {
    const result = extractReferencedNames('member.age + 1');
    check('a dotted path contributes only its first segment', JSON.stringify(result?.scopeNames) === JSON.stringify(['member']), JSON.stringify(result));
  }

  {
    const result = extractReferencedNames("lookup(pensionAgeFactor, age) + bonus");
    check('lookup()\'s first arg is a table name, not a scope name', JSON.stringify(result?.tableNames) === JSON.stringify(['pensionAgeFactor']), JSON.stringify(result));
    check('lookup()\'s remaining args are still scope names', JSON.stringify(result?.scopeNames.sort()) === JSON.stringify(['age', 'bonus']), JSON.stringify(result));
  }

  {
    const result = extractReferencedNames('1 +');
    check('an unparseable expression returns null', result === null);
  }

  // ── tryEvaluateFieldsForPreview ───────────────────────────────────────────
  {
    const calc: CalculationSet = { fields: { a: { expr: '1 + 2' } } };
    const { results } = tryEvaluateFieldsForPreview(calc, {});
    check('a field with no dependencies evaluates', results.a.status === 'ok' && results.a.display === '3', JSON.stringify(results));
  }

  {
    const calc: CalculationSet = { fields: { double: { expr: 'age * 2' } } };
    const { results } = tryEvaluateFieldsForPreview(calc, { age: 21 });
    check('a field referencing an input evaluates', results.double.status === 'ok' && results.double.display === '42', JSON.stringify(results));
  }

  {
    const calc: CalculationSet = {
      fields: {
        base: { expr: '10' },
        doubled: { expr: 'base * 2' },
      },
    };
    const { results } = tryEvaluateFieldsForPreview(calc, {});
    check('a field referencing an earlier field evaluates', results.doubled.status === 'ok' && results.doubled.display === '20', JSON.stringify(results));
  }

  {
    const calc: CalculationSet = {
      fields: {
        broken: { expr: '1 / 0' },
        unaffected: { expr: '5 + 5' },
      },
    };
    const { results } = tryEvaluateFieldsForPreview(calc, {});
    check('a failing field reports status:error', results.broken.status === 'error', JSON.stringify(results));
    check('a failing field does not poison an unrelated field', results.unaffected.status === 'ok' && results.unaffected.display === '10', JSON.stringify(results));
  }

  {
    const calc: CalculationSet = {
      fields: {
        member: { source: 'service' },
        age: { expr: 'member.age' },
      },
    };
    const { results } = tryEvaluateFieldsForPreview(calc, {});
    check('a service field reports status:service, no attempted evaluation', results.member.status === 'service', JSON.stringify(results));
    check('a field depending on an unsupplied service field fails, not silently succeeds', results.age.status === 'error', JSON.stringify(results));
  }

  {
    const calc: CalculationSet = { fields: { empty: { expr: '' } } };
    const { results } = tryEvaluateFieldsForPreview(calc, {});
    check('a field with an empty expression reports status:error', results.empty.status === 'error', JSON.stringify(results));
  }

  // ── tryEvaluateSeriesForPreview ───────────────────────────────────────────
  {
    const calc: CalculationSet = {};
    const result = tryEvaluateSeriesForPreview(
      { over: 'age', from: '1', to: '3', values: { doubled: 'age * 2' } },
      {},
      calc
    );
    check('a simple series produces the expected row count', result.status === 'ok' && result.rows.length === 3, JSON.stringify(result));
    check('a simple series computes each row correctly', result.status === 'ok' && result.rows[1].doubled === '4', JSON.stringify(result));
  }

  {
    const calc: CalculationSet = {};
    const result = tryEvaluateSeriesForPreview(
      { over: 'age', from: 'age', to: '3', values: {} },
      { age: 1 },
      calc
    );
    check("a loop variable colliding with an existing scope name is rejected", result.status === 'error', JSON.stringify(result));
  }

  {
    const calc: CalculationSet = {};
    const result = tryEvaluateSeriesForPreview(
      { over: 'x', from: '1', to: '5000', values: {} },
      {},
      calc
    );
    check('a series that would produce too many rows is rejected', result.status === 'error', JSON.stringify(result));
  }

  return failures;
}
