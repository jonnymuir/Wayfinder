import { serializeAuthoredServiceBlueprint, authoredServiceBlueprintJsonEquals } from './service-blueprint-canonical-json.js';
import type { AuthoredServiceBlueprint } from './types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function minimalBlueprint(overrides: Partial<AuthoredServiceBlueprint> = {}): AuthoredServiceBlueprint {
  return {
    definitionKey: 'fixture',
    displayName: 'Fixture',
    version: 1,
    initialStage: 'only',
    requestPolicy: 'single',
    stages: [{ stateKey: 'only', displayName: 'Only', queueKey: 'citizen', components: [] }],
    ...overrides,
  };
}

export function run(): number {
  failures = 0;

  // ── calculations.fields' own key order must survive verbatim ─────────────
  // Regression test: a real save through this exact path once turned a working calculation
  // set into a broken one, because sortKeys() alphabetised calculations.fields — silently
  // reordering "riskMultiplier" after "riskLoading" (which depends on it) purely because 'i'
  // sorts before 'l'. calculation-ordering.ts computes and preserves the real evaluation
  // order deliberately; this serializer must never disturb it.
  {
    const blueprint = minimalBlueprint({
      calculations: {
        fields: {
          zField: { expr: '1' },
          aField: { expr: 'zField + 1' },
        },
      },
    });

    const json = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = JSON.parse(json);
    const fieldOrder = Object.keys(parsed.calculations.fields);
    check(
      'calculations.fields key order is preserved exactly, not alphabetised',
      JSON.stringify(fieldOrder) === JSON.stringify(['zField', 'aField']),
      JSON.stringify(fieldOrder)
    );
  }

  {
    const blueprint = minimalBlueprint({
      calculations: {
        fields: { a: { expr: '1' } },
        tables: { zTable: { values: { '1': 1 } }, aTable: { values: { '1': 1 } } },
        series: { zSeries: { over: 'i', from: '1', to: '1', values: {} }, aSeries: { over: 'i', from: '1', to: '1', values: {} } },
      },
    });

    const json = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = JSON.parse(json);
    check('calculations.tables key order is preserved', JSON.stringify(Object.keys(parsed.calculations.tables)) === JSON.stringify(['zTable', 'aTable']));
    check('calculations.series key order is preserved', JSON.stringify(Object.keys(parsed.calculations.series)) === JSON.stringify(['zSeries', 'aSeries']));
  }

  // ── Everything else still gets deterministic key ordering ────────────────
  // A component (unlike a stage/gateway, which serialisableState/serialisableGateway rebuild
  // into a fixed shape) round-trips through sortKeys as-is, so it's the right shape to prove
  // non-calculations content is still alphabetised, with "type" pinned first for
  // System.Text.Json's polymorphic discriminator requirement.
  {
    const blueprint = minimalBlueprint({
      stages: [
        {
          stateKey: 'only',
          displayName: 'Only',
          queueKey: 'citizen',
          components: [{ type: 'text', zzz: 'last', aaa: 'first' }],
        } as unknown as AuthoredServiceBlueprint['stages'][0],
      ],
    });
    const json = serializeAuthoredServiceBlueprint(blueprint);
    const typeIndex = json.indexOf('"type"');
    const aaaIndex = json.indexOf('"aaa"');
    const zzzIndex = json.indexOf('"zzz"');
    check(
      'a component\'s own properties are still alphabetised, with "type" pinned first',
      typeIndex > -1 && typeIndex < aaaIndex && aaaIndex < zzzIndex,
      `type@${typeIndex} aaa@${aaaIndex} zzz@${zzzIndex}`
    );
  }

  // ── Equality comparison stays deterministic with calculations present ────
  {
    const blueprint = minimalBlueprint({ calculations: { fields: { b: { expr: '1' }, a: { expr: 'b + 1' } } } });
    const clone: AuthoredServiceBlueprint = JSON.parse(JSON.stringify(blueprint));
    check('two structurally-identical blueprints (including calculations) compare equal', authoredServiceBlueprintJsonEquals(blueprint, clone));
  }

  return failures;
}
