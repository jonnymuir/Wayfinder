import { computeCalculationDiagnostics, type CalculationDiagnosticsInput } from './calculation-diagnostics.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function base(overrides: Partial<CalculationDiagnosticsInput> = {}): CalculationDiagnosticsInput {
  return {
    fields: {},
    series: {},
    tableNames: new Set(),
    inScopeInputFieldKeys: new Set(),
    ...overrides,
  };
}

export function run(): number {
  failures = 0;

  // ── A valid, quiet blueprint ────────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { a: { expr: '1' }, b: { expr: 'a + age' } },
      inScopeInputFieldKeys: new Set(['age']),
    }));
    check('a valid, already-ordered set with a real input reference produces no diagnostics', diagnostics.length === 0, JSON.stringify(diagnostics));
  }

  // ── Field parse error ────────────────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({ fields: { a: { expr: '1 +' } } }));
    check('an unparseable field expression is flagged', diagnostics.some(d => d.kind === 'field-parse-error' && d.field === 'a'), JSON.stringify(diagnostics));
  }

  // ── Field unknown reference ──────────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({ fields: { a: { expr: 'mystery + 1' } } }));
    check(
      'a field referencing an unknown name is flagged',
      diagnostics.some(d => d.kind === 'field-unknown-reference' && d.field === 'a' && d.name === 'mystery'),
      JSON.stringify(diagnostics)
    );
  }

  // ── Field unknown table ──────────────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({ fields: { a: { expr: 'lookup(missingTable, 1)' } } }));
    check(
      'a lookup() against an unknown table is flagged',
      diagnostics.some(d => d.kind === 'field-unknown-table' && d.field === 'a' && d.table === 'missingTable'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { a: { expr: 'lookup(realTable, 1)' } },
      tableNames: new Set(['realTable']),
    }));
    check('a lookup() against a real table is not flagged', !diagnostics.some(d => d.kind === 'field-unknown-table'), JSON.stringify(diagnostics));
  }

  // ── Service fields are skipped entirely ──────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({ fields: { a: { source: 'service' } } }));
    check('a service-sourced field with no expr is not flagged', diagnostics.length === 0, JSON.stringify(diagnostics));
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({ fields: { a: { source: 'service', expr: 'this is not valid' } } }));
    check('a service-sourced field is never expression-checked, even with an expr present', diagnostics.length === 0, JSON.stringify(diagnostics));
  }

  // ── Field-name collision — the PR #40 bug, now centralised ───────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { totalPremium: { expr: '1' } },
      inScopeInputFieldKeys: new Set(), // totalPremium has no declared default — not in scope
    }));
    check(
      'a field name matching a NO-default input is NOT a collision (mirrors CalculationScopeBuilder.Build)',
      !diagnostics.some(d => d.kind === 'field-name-collision'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { averageAudienceSize: { expr: '1' } },
      inScopeInputFieldKeys: new Set(['averageAudienceSize']), // has a declared default — genuinely in scope
    }));
    check(
      'a field name matching a WITH-default input IS a collision',
      diagnostics.some(d => d.kind === 'field-name-collision' && d.field === 'averageAudienceSize'),
      JSON.stringify(diagnostics)
    );
  }

  // ── Field cycle ───────────────────────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { a: { expr: 'b + 1' }, b: { expr: 'a + 1' } },
    }));
    const cycle = diagnostics.find(d => d.kind === 'field-cycle');
    check('a genuine cycle is flagged, naming both fields', !!cycle && cycle.kind === 'field-cycle' && cycle.fields.sort().join(',') === 'a,b', JSON.stringify(diagnostics));
  }

  // ── Field declared out of order ───────────────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { b: { expr: 'a + 1' }, a: { expr: '1' } },
    }));
    check(
      'a forward reference (declared out of dependency order) is flagged, naming the field and what it must follow',
      diagnostics.some(d => d.kind === 'field-order' && d.field === 'b' && d.mustFollow === 'a'),
      JSON.stringify(diagnostics)
    );
  }

  // ── Series parse/reference/table errors ──────────────────────────────────
  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'i', from: '1 +', to: '5', values: { col: 'mystery' } } },
    }));
    check('an unparseable series "from" is flagged', diagnostics.some(d => d.kind === 'series-parse-error' && d.series === 's' && d.part === 'from'), JSON.stringify(diagnostics));
    check(
      'an unknown reference in a series column is flagged',
      diagnostics.some(d => d.kind === 'series-unknown-reference' && d.series === 's' && d.part === 'values' && d.column === 'col' && d.name === 'mystery'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'i', from: '1', to: '5', values: { col: 'lookup(missingTable, i)' } } },
    }));
    check(
      'a series column calling lookup() against an unknown table is flagged',
      diagnostics.some(d => d.kind === 'series-unknown-table' && d.series === 's' && d.table === 'missingTable'),
      JSON.stringify(diagnostics)
    );
  }

  // ── A series' own loop variable is valid inside its `values` columns, not `from`/`to` ──
  // (real bug caught live against juggling-insurance-modeller.json's premiumByFrequency series,
  // whose "frequency" column is `round(performances * 1.25)` where "performances" is `over`
  // itself — CalculationEvaluator.cs's EvaluateSeries adds `over` to the per-row scope only for
  // the values columns, evaluating from/to against the outer scope beforehand.)
  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'performances', from: '0', to: '50', values: { frequency: 'round(performances * 1.25)' } } },
    }));
    check(
      "a values column referencing its own series' loop variable is not flagged as unknown",
      !diagnostics.some(d => d.kind === 'series-unknown-reference'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'performances', from: 'performances', to: '50', values: {} } },
    }));
    check(
      "the loop variable is NOT valid inside 'from' (evaluated before the loop variable exists)",
      diagnostics.some(d => d.kind === 'series-unknown-reference' && d.part === 'from' && d.name === 'performances'),
      JSON.stringify(diagnostics)
    );
  }

  // ── Series loop-variable collision — new check, mirrors CalculationEvaluator.cs:153 ──
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { total: { expr: '1' } },
      series: { s: { over: 'total', from: '1', to: '5', values: {} } },
    }));
    check(
      'a loop variable colliding with an earlier field name is flagged',
      diagnostics.some(d => d.kind === 'series-loop-variable-collision' && d.series === 's' && d.variable === 'total'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'age', from: '1', to: '5', values: {} } },
      inScopeInputFieldKeys: new Set(['age']),
    }));
    check(
      'a loop variable colliding with an in-scope input is flagged',
      diagnostics.some(d => d.kind === 'series-loop-variable-collision' && d.series === 's' && d.variable === 'age'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      series: { s: { over: 'i', from: '1', to: '5', values: {} } },
      inScopeInputFieldKeys: new Set(['age']),
    }));
    check('a loop variable with no collision is not flagged', !diagnostics.some(d => d.kind === 'series-loop-variable-collision'), JSON.stringify(diagnostics));
  }

  // ── Unknown-reference scoping is corrected: a no-default input is NOT "known" ──
  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { a: { expr: 'noDefaultInput + 1' } },
      inScopeInputFieldKeys: new Set(), // noDefaultInput deliberately excluded — no declared default
    }));
    check(
      'a reference to an input with no declared default is flagged as unknown (statically unresolvable at Validate() time)',
      diagnostics.some(d => d.kind === 'field-unknown-reference' && d.name === 'noDefaultInput'),
      JSON.stringify(diagnostics)
    );
  }

  {
    const diagnostics = computeCalculationDiagnostics(base({
      fields: { a: { expr: 'hasDefaultInput + 1' } },
      inScopeInputFieldKeys: new Set(['hasDefaultInput']),
    }));
    check('a reference to an input WITH a declared default is not flagged', !diagnostics.some(d => d.kind === 'field-unknown-reference'), JSON.stringify(diagnostics));
  }

  return failures;
}
