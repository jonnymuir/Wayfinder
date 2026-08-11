import type { ComponentDescriptor } from './types.js';
import { generateComponentJsonSchema } from './component-json-schema.js';
import { lintAuthoredServiceBlueprintDocument } from './service-blueprint-lint.js';

const CATALOG: ComponentDescriptor[] = [
  {
    discriminator: 'text',
    displayName: 'Text input',
    category: 'Input',
    clrType: 'TextInputComponent',
    isInput: true,
    properties: [
      { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
      { key: 'label', title: 'Label', valueKind: 'String', required: true },
      { key: 'conditionalOn', title: 'Conditional on field', valueKind: 'String', required: false, format: 'field-ref' },
      { key: 'defaultFrom', title: 'Default from calculation', valueKind: 'String', required: false, format: 'calculation-ref' },
      { key: 'changeStateKey', title: 'Change link target stage', valueKind: 'String', required: false, format: 'stage-ref' },
    ],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'number',
    displayName: 'Number input',
    category: 'Input',
    clrType: 'NumberInputComponent',
    isInput: true,
    properties: [
      { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
      { key: 'label', title: 'Label', valueKind: 'String', required: true },
    ],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'heading',
    displayName: 'Heading',
    category: 'Content',
    clrType: 'HeadingComponent',
    isInput: false,
    properties: [
      { key: 'content', title: 'Content', valueKind: 'String', required: true },
      { key: 'level', title: 'Level', valueKind: 'Integer', required: false, minimum: 1, maximum: 6 },
    ],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'fieldset',
    displayName: 'Fieldset',
    category: 'Container',
    clrType: 'FieldsetComponent',
    isInput: false,
    properties: [{ key: 'legend', title: 'Legend', valueKind: 'String', required: false }],
    containment: { kind: 'ChildList', propertyName: 'children' },
  },
  {
    discriminator: 'radio',
    displayName: 'Radios',
    category: 'Input',
    clrType: 'RadiosComponent',
    isInput: true,
    properties: [
      { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
      { key: 'label', title: 'Label', valueKind: 'String', required: true },
      { key: 'options', title: 'Options', valueKind: 'StringArray', required: true },
    ],
    // propertyName/keySourceProperty are camelCase here too, matching what a live host actually
    // sends (see ComponentDescriptor.cs's PropertyNameJsonConverter) — not the C#-internal
    // "ConditionalChildren"/"Options" nameof() values.
    containment: { kind: 'KeyedChildren', propertyName: 'conditionalChildren', keySourceProperty: 'options' },
  },
];

function minimalBlueprint(components: unknown): Record<string, unknown> {
  return {
    definitionKey: 'fixture',
    displayName: 'Fixture',
    initialStage: 'only',
    queues: [],
    gateways: [],
    stages: [
      { stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components },
    ],
  };
}

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

  // ── generateComponentJsonSchema ──────────────────────────────────────────
  {
    const schema = generateComponentJsonSchema(CATALOG);
    const defs = schema.$defs as Record<string, unknown>;

    check('schema: has a $defs entry per discriminator', CATALOG.every(d => d.discriminator in defs));

    const textDef = defs.text as Record<string, unknown>;
    const textProperties = textDef.properties as Record<string, unknown>;
    check('schema: a leaf type declares its own properties', 'fieldKey' in textProperties && 'label' in textProperties);
    check('schema: required properties are listed', (textDef.required as string[]).includes('fieldKey'));

    const fieldsetDef = defs.fieldset as Record<string, unknown>;
    const fieldsetProperties = fieldsetDef.properties as Record<string, unknown>;
    check('schema: a ChildList container schema includes its children slot',
      'children' in fieldsetProperties);

    const componentDef = defs.component as Record<string, unknown>;
    const oneOf = componentDef.oneOf as Array<{ $ref: string }>;
    check('schema: the polymorphic component def has one oneOf branch per discriminator',
      oneOf.length === CATALOG.length);
  }

  // ── lintAuthoredServiceBlueprintDocument — component checks ──────────────
  {
    const parsed = minimalBlueprint([{ type: 'text', fieldKey: 'name', label: 'Name' }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a valid component produces no issues', issues.length === 0, JSON.stringify(issues));
  }

  {
    const parsed = minimalBlueprint([{ type: 'made-up-type', fieldKey: 'name' }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: an unknown component type is flagged',
      issues.some(issue => issue.message.includes('Unknown component type')));
  }

  {
    const parsed = minimalBlueprint([{ type: 'text', fieldKey: '', label: '' }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: an empty required property is flagged',
      issues.filter(issue => issue.message.includes('is required')).length === 2,
      JSON.stringify(issues));
  }

  {
    const parsed = minimalBlueprint([{ type: 'heading', content: 'Section', level: 9 }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a numeric property above its maximum is flagged',
      issues.some(issue => issue.message.includes('at most 6')));
  }

  {
    const parsed = minimalBlueprint([{
      type: 'fieldset',
      legend: 'Group',
      children: [{ type: 'text', fieldKey: '', label: 'Name' }],
    }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: recurses into a ChildList child and flags its own issue',
      issues.some(issue => issue.pathHint?.includes('children[0].fieldKey')),
      JSON.stringify(issues));
  }

  {
    const parsed = minimalBlueprint([{
      type: 'radio',
      fieldKey: 'choice',
      label: 'Choice',
      options: ['Yes', 'No'],
      conditionalChildren: { Maybe: [{ type: 'text', fieldKey: 'why', label: 'Why?' }] },
    }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a KeyedChildren key not in Options is flagged',
      issues.some(issue => issue.message.includes('"Maybe" is a key')));
  }

  {
    const parsed = minimalBlueprint([{ type: 'made-up-type' }]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed));
    check('lint: component checks are skipped entirely when no catalog is supplied (back-compat default)',
      issues.length === 0);
  }

  // ── field-ref/calculation-ref/stage-ref dangling-reference checks ────────
  {
    const parsed = minimalBlueprint([
      { type: 'text', fieldKey: 'name', label: 'Name' },
      { type: 'text', fieldKey: 'nickname', label: 'Nickname', conditionalOn: 'nam' },
    ]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a conditionalOn not matching a sibling fieldKey is flagged',
      issues.some(issue => issue.pathHint?.includes('[1].conditionalOn') && issue.message.includes('"nam"')),
      JSON.stringify(issues));
  }

  {
    const parsed = minimalBlueprint([
      { type: 'text', fieldKey: 'name', label: 'Name' },
      { type: 'text', fieldKey: 'nickname', label: 'Nickname', conditionalOn: 'name' },
    ]);
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a conditionalOn matching a real sibling fieldKey produces no issue for it',
      !issues.some(issue => issue.pathHint?.includes('conditionalOn')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { suggestedName: { expr: '1' } } },
      stages: [{
        stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question',
        components: [{ type: 'text', fieldKey: 'name', label: 'Name', defaultFrom: 'suggestdName' }],
      }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a defaultFrom not matching a calculations.fields name is flagged',
      issues.some(issue => issue.pathHint?.includes('defaultFrom') && issue.message.includes('"suggestdName"')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'first', queues: [], gateways: [],
      stages: [
        { stageKey: 'first', displayName: 'First', queueKey: 'citizen', stageType: 'Question', components: [] },
        {
          stageKey: 'second', displayName: 'Second', queueKey: 'citizen', stageType: 'Question',
          components: [{ type: 'text', fieldKey: 'name', label: 'Name', changeStateKey: 'frist' }],
        },
      ],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a changeStateKey not matching a real stage key is flagged',
      issues.some(issue => issue.pathHint?.includes('changeStateKey') && issue.message.includes('"frist"')),
      JSON.stringify(issues));
  }

  // ── calculations.fields/series checks (mirror the Calculations tab's own live checks) ────
  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { a: { expr: '1 +' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: an unparseable calculations.fields expression is flagged',
      issues.some(issue => issue.pathHint === 'calculations.fields.a'),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { a: { expr: 'nosuchname + 1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a calculations.fields expression referencing an unknown name is flagged',
      issues.some(issue => issue.message.includes('"nosuchname"')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { a: { expr: "lookup(nosuchtable, 1)" } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a lookup() call against an unknown table is flagged',
      issues.some(issue => issue.message.includes('unknown table "nosuchtable"')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { b: { expr: 'a + 1' }, a: { expr: '1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: calculations.fields declared out of dependency order is flagged',
      issues.some(issue => issue.pathHint === 'calculations.fields' && issue.message.includes('out of dependency order')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { a: { expr: 'b + 1' }, b: { expr: 'a + 1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a genuine calculations.fields cycle is flagged by name',
      issues.some(issue => issue.pathHint === 'calculations.fields' && issue.message.includes('circular dependency')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: {
        fields: { a: { expr: '1' } },
        series: { s: { over: 'i', from: '1', to: '3', values: { x: 'nosuchname2' } } },
      },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a series value expression referencing an unknown name is flagged',
      issues.some(issue => issue.pathHint === 'calculations.series.s.values.x' && issue.message.includes('"nosuchname2"')),
      JSON.stringify(issues));
  }

  // ── field-name/loop-variable collision (shared with the Calculations tab and the Validation
  // tab via calculation-diagnostics.ts — see PR #40 and the validation-unification follow-up) ──
  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { age: { expr: '1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [
        { type: 'text', fieldKey: 'age', label: 'Age', default: '30' },
      ] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a calculations.fields name colliding with a WITH-default input fieldKey is flagged',
      issues.some(issue => issue.pathHint === 'calculations.fields.age' && issue.message.includes('collides with an input')),
      JSON.stringify(issues));
  }

  {
    // A numeric input with no declared default is the one case CalculationScopeBuilder.Build
    // still leaves genuinely absent from scope (no safe placeholder for a missing amount) — so a
    // calc field sharing its name is not a real collision. A text/boolean field with no default
    // WOULD now be a genuine collision (it always resolves, to "" / false), so this fixture must
    // stay numeric to test what it claims to.
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { totalPremium: { expr: '1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [
        { type: 'number', fieldKey: 'totalPremium', label: 'Total premium' },
      ] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a calculations.fields name matching a NO-default NUMERIC input fieldKey is NOT flagged as a collision',
      !issues.some(issue => issue.message.includes('collides with an input')),
      JSON.stringify(issues));
  }

  {
    // A text/boolean field with no declared default now IS a genuine collision — it always
    // resolves in scope (to "" / false), matching CalculationScopeBuilder.Build server-side.
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { totalPremium: { expr: "'1'" } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [
        { type: 'text', fieldKey: 'totalPremium', label: 'Total premium' },
      ] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a calculations.fields name matching a NO-default TEXT input fieldKey IS flagged as a collision',
      issues.some(issue => issue.message.includes('collides with an input')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: {
        fields: { total: { expr: '1' } },
        series: { s: { over: 'total', from: '1', to: '3', values: {} } },
      },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a series loop variable colliding with an earlier field name is flagged',
      issues.some(issue => issue.pathHint === 'calculations.series.s.over' && issue.message.includes('collides with an existing')),
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: {
        series: { premiumByFrequency: { over: 'performances', from: '0', to: '50', values: { frequency: 'round(performances * 1.25)' } } },
      },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check("lint: a series' own loop variable is valid inside its values columns (real juggling-insurance-modeller.json shape)",
      issues.length === 0,
      JSON.stringify(issues));
  }

  {
    const parsed = {
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { a: { expr: '1' }, b: { expr: 'a + 1' } } },
      stages: [{ stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question', components: [] }],
    };
    const issues = lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    check('lint: a valid, already-correctly-ordered calculations.fields block produces no issues',
      !issues.some(issue => issue.pathHint?.startsWith('calculations')),
      JSON.stringify(issues));
  }

  if (failures > 0) {
    console.error(`\n${failures} component schema/lint check(s) failed.`);
  } else {
    console.log('\nAll component schema/lint checks passed.');
  }
  return failures;
}
