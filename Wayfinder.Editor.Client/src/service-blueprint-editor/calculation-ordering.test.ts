import { computeStableFieldOrder, type FieldInput } from './calculation-ordering.js';

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

  // ── No dependencies ───────────────────────────────────────────────────────
  {
    const fields: FieldInput[] = [{ name: 'a', expr: '1' }, { name: 'b', expr: '2' }];
    const result = computeStableFieldOrder(fields, ['a', 'b']);
    check('independent fields keep their original order', result.ok && JSON.stringify(result.order) === JSON.stringify(['a', 'b']), JSON.stringify(result));
    check('nothing reported as moved when already valid', result.ok && result.moved.length === 0, JSON.stringify(result));
  }

  // ── A simple chain, already valid ─────────────────────────────────────────
  {
    const fields: FieldInput[] = [
      { name: 'a', expr: '1' },
      { name: 'b', expr: 'a + 1' },
      { name: 'c', expr: 'b + 1' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'b', 'c']);
    check('an already-valid chain is unchanged', result.ok && JSON.stringify(result.order) === JSON.stringify(['a', 'b', 'c']), JSON.stringify(result));
  }

  // ── A forward reference — must reorder and explain why ───────────────────
  {
    const fields: FieldInput[] = [
      { name: 'b', expr: 'a + 1' },
      { name: 'a', expr: '1' },
    ];
    const result = computeStableFieldOrder(fields, ['b', 'a']);
    check('a forward reference is fixed by moving the dependent field later', result.ok && JSON.stringify(result.order) === JSON.stringify(['a', 'b']), JSON.stringify(result));
    check('the move is reported, naming the field and the dependency', result.ok && result.moved.length === 1 && result.moved[0].name === 'b' && result.moved[0].movedAfter === 'a', JSON.stringify(result));
  }

  // ── A diamond dependency, already valid ───────────────────────────────────
  {
    const fields: FieldInput[] = [
      { name: 'a', expr: '1' },
      { name: 'b', expr: 'a + 1' },
      { name: 'c', expr: 'a + 2' },
      { name: 'd', expr: 'b + c' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'b', 'c', 'd']);
    check('a diamond dependency in valid order is unchanged', result.ok && JSON.stringify(result.order) === JSON.stringify(['a', 'b', 'c', 'd']), JSON.stringify(result));
  }

  // ── A genuine cycle ────────────────────────────────────────────────────────
  {
    const fields: FieldInput[] = [
      { name: 'a', expr: 'b + 1' },
      { name: 'b', expr: 'a + 1' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'b']);
    check('a two-field cycle is detected, not silently ordered', result.ok === false, JSON.stringify(result));
    check('the cycle names both fields involved', !result.ok && result.cycle.sort().join(',') === 'a,b', JSON.stringify(result));
  }

  {
    const fields: FieldInput[] = [
      { name: 'a', expr: 'b + 1' },
      { name: 'b', expr: 'c + 1' },
      { name: 'c', expr: 'a + 1' },
      { name: 'unrelated', expr: '1' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'b', 'c', 'unrelated']);
    check('a three-field cycle is detected and excludes an unrelated field', !result.ok && result.cycle.sort().join(',') === 'a,b,c', JSON.stringify(result));
  }

  // ── References that must NOT affect ordering ──────────────────────────────
  {
    const fields: FieldInput[] = [{ name: 'a', expr: 'age * 2' }];
    const result = computeStableFieldOrder(fields, ['a']);
    check('a reference to an input (not another field) does not create a dependency edge', result.ok && JSON.stringify(result.order) === JSON.stringify(['a']), JSON.stringify(result));
  }

  {
    const fields: FieldInput[] = [
      { name: 'b', expr: 'lookup(pensionAgeFactor, age)' },
      { name: 'pensionAgeFactor', expr: '1' },
    ];
    const result = computeStableFieldOrder(fields, ['b', 'pensionAgeFactor']);
    check(
      "a lookup() table-name reference does not create a dependency edge, even if a field happens to share that name",
      result.ok && JSON.stringify(result.order) === JSON.stringify(['b', 'pensionAgeFactor']),
      JSON.stringify(result)
    );
  }

  // ── New fields ─────────────────────────────────────────────────────────────
  {
    const fields: FieldInput[] = [
      { name: 'a', expr: '1' },
      { name: 'b', expr: 'a + 1' },
      { name: 'brandNew', expr: 'a + 5' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'b']);
    check('a brand-new field (not in currentOrder) is appended, not treated as moved', result.ok && JSON.stringify(result.order) === JSON.stringify(['a', 'b', 'brandNew']), JSON.stringify(result));
    check('a brand-new field is never reported in `moved`', result.ok && result.moved.length === 0, JSON.stringify(result));
  }

  // ── An unparseable expression doesn't crash ordering ──────────────────────
  {
    const fields: FieldInput[] = [
      { name: 'a', expr: '1' },
      { name: 'broken', expr: '1 +' },
    ];
    const result = computeStableFieldOrder(fields, ['a', 'broken']);
    check('a field with a currently-unparseable expression is treated as having no dependencies, not an error', result.ok && result.order.includes('broken'), JSON.stringify(result));
  }

  // ── Stability: minimal reordering only moves what's forced ────────────────
  {
    const fields: FieldInput[] = [
      { name: 'z', expr: '1' },
      { name: 'y', expr: '2' },
      { name: 'x', expr: 'z + 1' },
    ];
    const result = computeStableFieldOrder(fields, ['z', 'y', 'x']);
    check('fields with no dependency relationship keep their relative order (z, y unaffected by x)', result.ok && JSON.stringify(result.order) === JSON.stringify(['z', 'y', 'x']), JSON.stringify(result));
  }

  return failures;
}
