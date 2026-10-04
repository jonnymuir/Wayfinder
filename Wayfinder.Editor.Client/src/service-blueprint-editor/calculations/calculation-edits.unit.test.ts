import {
  changedName,
  fieldEdits,
  fieldNameError,
  inDependencyOrder,
  omitKey,
  renameKey,
  seriesEdits,
  tableEdits,
  uniqueName,
  validationEdits,
} from './calculation-edits.js';
import type { StageDefinition } from '../types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

const keys = (record: object) => Object.keys(record).join();

export function run(): number {
  failures = 0;

  check('a new name takes the first unused number', uniqueName('field', { field1: 1, field2: 2 }) === 'field3');
  check('an unused gap is reused', uniqueName('field', { field2: 2 }) === 'field1');
  check('renaming a key keeps the entry where it was', keys(renameKey({ a: 1, b: 2, c: 3 }, 'b', 'x')) === 'a,x,c');
  check('omitting a key leaves the others', keys(omitKey({ a: 1, b: 2 }, 'a')) === 'b');
  check('a blank or unchanged name is not a rename', changedName('a', '  ') === null && changedName('a', ' a ') === null);
  check('a changed name is trimmed', changedName('a', ' b ') === 'b');

  const added = fieldEdits.add({ field1: { expr: '1' } });
  check('adding a field gives it an empty expression and the next name', added.name === 'field2' && added.fields.field2.expr === '');
  check(
    'switching a field to service keeps its valueKind and default',
    (() => {
      const next = fieldEdits.setSource({ f: { expr: '1', valueKind: 'number', default: '3' } as never }, 'f', true).f as Record<
        string,
        unknown
      >;
      return next.source === 'service' && next.valueKind === 'number' && next.default === '3' && !('expr' in next);
    })()
  );
  check(
    'switching a field back to authored clears the service settings',
    keys(fieldEdits.setSource({ f: { source: 'service' } }, 'f', false).f) === 'expr'
  );
  check('clearing a format removes it', fieldEdits.setFormat({ f: { expr: '1', format: 'gbp' } }, 'f', '').f.format === undefined);

  check(
    'field names must be present, non-colliding and unique',
    fieldNameError('', [], new Set()) !== null &&
      fieldNameError('a', ['a'], new Set(['a'])) !== null &&
      fieldNameError('a', ['a', 'a'], new Set()) === 'Duplicate field name.' &&
      fieldNameError('a', ['a'], new Set()) === null
  );

  const ordered = inDependencyOrder({ b: { expr: 'a + 1' }, a: { expr: '1' } }, ['b', 'a']);
  check('a field that depends on a later one is moved after it', keys(ordered.fields) === 'a,b' && ordered.announcement !== null);
  check('a cycle leaves the fields as typed', keys(inDependencyOrder({ a: { expr: 'b' }, b: { expr: 'a' } }, ['a', 'b']).fields) === 'a,b');

  check('a new table starts linear and empty', JSON.stringify(tableEdits.add({}).tables.table1) === '{"interpolate":"linear","values":{}}');
  check(
    'a new table row takes the first unused whole number',
    Object.keys(tableEdits.addRow({ t: { values: { '0': 1, '1': 2 } } }, 't').t.values).join() === '0,1,2'
  );
  check(
    'renaming a table row keeps its value and position',
    JSON.stringify(tableEdits.renameRow({ t: { values: { a: 1, b: 2 } } }, 't', 'a', 'z').t.values) === '{"z":1,"b":2}'
  );

  const twoColumns = seriesEdits.addColumn(seriesEdits.addColumn({ s: { over: 'i', from: '1', to: '3', values: {} } }, 's'), 's');
  check('adding two columns to a series gives two distinct columns', keys(twoColumns.s.values) === 'column1,column2');
  check(
    'a series column can be renamed and its expression set',
    (() => {
      const renamed = seriesEdits.renameColumn(twoColumns, 's', 'column1', 'total');
      return seriesEdits.setColumnExpr(renamed, 's', 'total', 'i * 2').s.values.total === 'i * 2';
    })()
  );

  const stage = {
    stageKey: 's',
    displayName: 'S',
    queueKey: 'q',
    components: [],
    validations: [{ code: 'a', rule: 'x', message: 'm' }],
  } as StageDefinition;
  check('a new validation rule starts blank', JSON.stringify(validationEdits.add(stage)[1]) === '{"code":"","rule":"","message":""}');
  check(
    'a validation rule can be patched and removed',
    validationEdits.patch(stage, 0, { code: 'b' })[0].code === 'b' && validationEdits.remove(stage, 0).length === 0
  );

  return failures;
}
