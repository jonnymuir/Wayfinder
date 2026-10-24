import { authoredServiceBlueprintEquals, parseYaml, serializeAuthoredServiceBlueprint } from './service-blueprint-yaml.js';
import type { ServiceBlueprint } from './types.js';
import { hydrateServiceBlueprintDefinition } from './blueprint-hydration.js';

let failures = 0;

// biome-ignore lint/suspicious/noExplicitAny: a test reads back arbitrary shapes from the YAML it wrote.
function fromYaml(text: string): any {
  const parsed = parseYaml(text);
  if (!parsed.ok) {
    throw new Error(`line ${parsed.line}: ${parsed.message}`);
  }
  return parsed.value;
}

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function minimalBlueprint(overrides: Partial<ServiceBlueprint> = {}): ServiceBlueprint {
  return {
    definitionKey: 'fixture',
    displayName: 'Fixture',
    version: 1,
    initialStage: 'only',
    requestPolicy: 'single',
    stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', components: [] }],
    ...overrides,
  };
}

export function run(): number {
  failures = 0;

  // ── written the way the server writes it ─────────────────────────────────
  {
    const yaml = serializeAuthoredServiceBlueprint(
      minimalBlueprint({
        queues: [{ key: 'citizen', displayName: 'Citizen' }],
        stages: [
          {
            stageKey: 'only',
            displayName: 'Only',
            queueKey: 'citizen',
            components: [{ label: 'Your name', required: true, fieldKey: 'name', type: 'text' }],
          },
        ],
      })
    );
    check(
      'a component opens with its type, then its fieldKey and label',
      yaml.includes('      - type: text\n        fieldKey: name\n        label: Your name\n        required: true\n'),
      yaml
    );
    check('queues come before stages', yaml.indexOf('queues:') < yaml.indexOf('stages:'), yaml);
  }

  {
    const yaml = serializeAuthoredServiceBlueprint(minimalBlueprint({ description: 'First line.\nSecond line.' }));
    check('multi-line text is a literal block', yaml.includes('description: |-\n  First line.\n  Second line.\n'), yaml);
  }

  {
    const shared = { type: 'text' as const, fieldKey: 'name', label: 'Name' };
    const yaml = serializeAuthoredServiceBlueprint(
      minimalBlueprint({
        stages: [
          { stageKey: 'a', displayName: 'A', queueKey: 'citizen', components: [shared] },
          { stageKey: 'b', displayName: 'B', queueKey: 'citizen', components: [shared] },
        ],
      })
    );
    check('a value used twice is written out twice, never as an anchor', !yaml.includes('&') && !yaml.includes('*'), yaml);
  }

  {
    const parsed = parseYaml('definitionKey: &k apply\ndisplayName: *k\n');
    check('anchors and aliases are refused', !parsed.ok && parsed.line === 1 && parsed.message.includes('Anchors and aliases'));
  }

  {
    const parsed = parseYaml('definitionKey: apply\nstages: [unclosed\n');
    check('a syntax error is reported where it is found, not at the top', !parsed.ok && parsed.line >= 2, JSON.stringify(parsed));
  }

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

    const yaml = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = fromYaml(yaml);
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

    const yaml = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = fromYaml(yaml);
    check(
      'calculations.tables key order is preserved',
      JSON.stringify(Object.keys(parsed.calculations.tables)) === JSON.stringify(['zTable', 'aTable'])
    );
    check(
      'calculations.series key order is preserved',
      JSON.stringify(Object.keys(parsed.calculations.series)) === JSON.stringify(['zSeries', 'aSeries'])
    );
  }

  // ── Equality comparison stays deterministic with calculations present ────
  {
    const blueprint = minimalBlueprint({ calculations: { fields: { b: { expr: '1' }, a: { expr: 'b + 1' } } } });
    const clone: ServiceBlueprint = structuredClone(blueprint);
    check('two structurally-identical blueprints (including calculations) compare equal', authoredServiceBlueprintEquals(blueprint, clone));
  }

  // ── Stage validations round-trip ──────────────────────────────────────────
  {
    const blueprint = minimalBlueprint({
      stages: [
        {
          stageKey: 'only',
          displayName: 'Only',
          queueKey: 'citizen',
          components: [],
          validations: [{ code: 'evidence-required', when: 'hasIssue', rule: 'hasEvidence', field: 'notes', message: 'Add detail.' }],
        },
      ],
    });

    const yaml = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = fromYaml(yaml);
    const rule = parsed.stages[0].validations?.[0];
    check(
      'a stage validation rule serializes with its when/rule/field/message intact',
      rule?.code === 'evidence-required' &&
        rule?.when === 'hasIssue' &&
        rule?.rule === 'hasEvidence' &&
        rule?.field === 'notes' &&
        rule?.message === 'Add detail.',
      JSON.stringify(rule)
    );
  }

  {
    const blueprint = minimalBlueprint();
    const yaml = serializeAuthoredServiceBlueprint(blueprint);
    const parsed = fromYaml(yaml);
    check('a stage with no validations omits the key entirely', !('validations' in parsed.stages[0]));
  }

  {
    const loaded = hydrateServiceBlueprintDefinition(structuredClone(minimalBlueprint({ allowManualRestart: true })));
    const parsed = fromYaml(serializeAuthoredServiceBlueprint(loaded));
    check(
      'allowManualRestart survives a load and save round trip',
      parsed.allowManualRestart === true,
      JSON.stringify(parsed.allowManualRestart)
    );
  }

  {
    const parsed = fromYaml(serializeAuthoredServiceBlueprint(minimalBlueprint()));
    check('a blueprint that has not opted in to manual restart omits the key', !('allowManualRestart' in parsed));
  }

  {
    const handoff = { id: 'a-to-b', fromState: 'a', toState: 'b', label: 'A to B', actorChange: 'caseworker' };
    const loaded = hydrateServiceBlueprintDefinition(
      structuredClone(
        minimalBlueprint({
          authoredServiceBlueprintId: '2c1b6f1e-0000-4000-8000-000000000001',
          handoffs: [handoff],
          tags: { demo: 'x' },
        })
      )
    );
    const parsed = fromYaml(serializeAuthoredServiceBlueprint(loaded));
    check('tags survive a load and save round trip', parsed.tags?.demo === 'x', JSON.stringify(parsed.tags));
    check(
      'handoffs survive a load and save round trip',
      parsed.handoffs?.length === 1 && Object.entries(handoff).every(([key, value]) => parsed.handoffs[0][key] === value),
      JSON.stringify(parsed.handoffs)
    );
    check(
      'authoredServiceBlueprintId survives a load and save round trip',
      parsed.authoredServiceBlueprintId === '2c1b6f1e-0000-4000-8000-000000000001',
      JSON.stringify(parsed.authoredServiceBlueprintId)
    );
  }

  {
    const parsed = fromYaml(serializeAuthoredServiceBlueprint(minimalBlueprint()));
    check(
      'a blueprint with no tags, handoffs or id omits those keys',
      !('tags' in parsed) && !('handoffs' in parsed) && !('authoredServiceBlueprintId' in parsed),
      JSON.stringify(parsed)
    );
  }

  return failures;
}
