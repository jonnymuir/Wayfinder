#!/usr/bin/env node
// Runs the shared conformance fixtures (../../Wayfinder/calculation-fixtures/calculation-golden.json)
// against wayfinder-calculations.js — the JS mirror of CalculationGoldenTests.cs, which runs the
// exact same fixture file against the C# evaluator. Any behavioural drift between the two runtimes
// must show up here or there as a failure. Plain Node, no test framework or npm install required —
// matches this package's own no-build-step convention for its static JS assets.
//
// Usage: node test/wayfinder-calculations.conformance.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { Dec, evaluateCalculations, toScope } from '../wwwroot/js/wayfinder-calculations.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturePath = join(__dirname, '..', '..', 'Wayfinder', 'calculation-fixtures', 'calculation-golden.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));

let passed = 0;
let failed = 0;

for (const testCase of fixture.cases) {
  try {
    runCase(testCase);
    passed++;
  } catch (error) {
    failed++;
    console.error(`FAIL: ${testCase.name}\n  ${error.message}`);
  }
}

console.log(`\n${passed} passed, ${failed} failed, ${fixture.cases.length} total.`);
process.exit(failed === 0 ? 0 : 1);

function runCase(testCase) {
  // Single-expression sugar: { "expr": "1 + 2" } becomes a set with one field "result" —
  // mirrors CalculationGoldenTests.BuildCalculationSet's own sugar handling.
  const set = 'expr' in testCase
    ? { tables: testCase.tables, fields: { result: { expr: testCase.expr } } }
    : { tables: testCase.tables, fields: testCase.fields ?? {}, series: testCase.series };

  const inputs = toScope(testCase.inputs ?? {});

  if (testCase.expectError) {
    let threw = false;
    try {
      evaluateCalculations(set, inputs);
    } catch {
      threw = true;
    }

    assertTrue(threw, `expected an error but none was thrown`);
    return;
  }

  const result = evaluateCalculations(set, inputs);

  if ('expect' in testCase) {
    assertValue(result.fields.result, testCase.expect, 'result');
  }

  if (testCase.expectFields) {
    for (const [name, expected] of Object.entries(testCase.expectFields)) {
      assertTrue(name in result.fields, `expected field '${name}' to be present`);
      assertValue(result.fields[name], expected, name);
    }
  }

  if (testCase.expectSeries) {
    for (const [name, expectedRows] of Object.entries(testCase.expectSeries)) {
      assertTrue(name in result.series, `expected series '${name}' to be present`);
      const rows = result.series[name];
      assertTrue(
        rows.length === expectedRows.length,
        `series '${name}' row count: expected ${expectedRows.length}, got ${rows.length}`,
      );

      for (let i = 0; i < expectedRows.length; i++) {
        for (const [column, expected] of Object.entries(expectedRows[i])) {
          assertValue(rows[i][column], expected, `${name}[${i}].${column}`);
        }
      }
    }
  }
}

// Numbers are asserted by decimal value, not string identity — "1.0" and "1" are equal, same as
// CalculationGoldenTests.AssertValue's decimal.Parse-then-compare.
function assertValue(actual, expected, context) {
  if (typeof expected === 'boolean') {
    assertTrue(actual === expected, `${context}: expected ${expected}, got ${actual}`);
    return;
  }

  if (typeof expected === 'string' && actual instanceof Dec) {
    const expectedDec = Dec.fromString(expected);
    assertTrue(actual.eq(expectedDec), `${context}: expected ${expected}, got ${actual.toString()}`);
    return;
  }

  assertTrue(actual === expected, `${context}: expected '${expected}', got '${actual}'`);
}

function assertTrue(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
