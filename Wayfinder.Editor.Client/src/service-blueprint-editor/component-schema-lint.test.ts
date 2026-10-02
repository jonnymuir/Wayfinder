import type { ComponentDescriptor } from './types.js';
import { generateComponentJsonSchema } from './component-json-schema.js';
import { coerceParsedAuthoredServiceBlueprint, lintAuthoredServiceBlueprintDocument } from './service-blueprint-lint.js';
import { EDITOR_TOP_LEVEL_FIELDS, serializeAuthoredServiceBlueprint } from './service-blueprint-canonical-json.js';

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

export function run(csharpServiceBlueprintSource: string): number {
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
    const applied = coerceParsedAuthoredServiceBlueprint({ ...minimalBlueprint([]), allowManualRestart: true });
    check('a Definition-tab edit that sets allowManualRestart true is kept when applied and saved',
      JSON.parse(serializeAuthoredServiceBlueprint(applied)).allowManualRestart === true,
      serializeAuthoredServiceBlueprint(applied));
    const notOptedIn = coerceParsedAuthoredServiceBlueprint(minimalBlueprint([]));
    check('a Definition-tab edit that omits allowManualRestart saves without it',
      !('allowManualRestart' in JSON.parse(serializeAuthoredServiceBlueprint(notOptedIn))));
  }

  // ── top-level fields: every field the editor owns survives apply+save and is type-checked ──
  {
    const SAMPLES: Record<string, { good: unknown; bad: unknown }> = {
      definitionKey: { good: 'k', bad: 7 },
      displayName: { good: 'D', bad: 7 },
      version: { good: 3, bad: '3' },
      initialStage: { good: 'only', bad: 7 },
      requestPolicy: { good: 'prompt', bad: 7 },
      allowManualRestart: { good: true, bad: 'true' },
      description: { good: 'text', bad: 7 },
      schemaVersion: { good: '1', bad: 7 },
      calculations: { good: { fields: { a: { expr: '1' } } }, bad: [] },
      queues: { good: [{ key: 'citizen', actor: 'citizen', displayName: 'Citizen' }], bad: {} },
      stages: { good: minimalBlueprint([]).stages, bad: {} },
      gateways: { good: [], bad: {} },
      parameterSchemas: { good: [], bad: {} },
      layout: { good: { nodes: { 'stage:only': { x: 1, y: 2 } } }, bad: [] },
      authoredServiceBlueprintId: { good: '2c1b6f1e-0000-4000-8000-000000000001', bad: 7 },
      handoffs: { good: [{ id: 'h', fromState: 'a', toState: 'b', label: 'L' }], bad: {} },
      tags: { good: { demo: 'x' }, bad: [] },
    };
    for (const key of Object.keys(EDITOR_TOP_LEVEL_FIELDS)) {
      const sample = SAMPLES[key];
      check(`top-level "${key}" has a test sample (declare one when adding a field)`, sample !== undefined);
      if (!sample) continue;
      const withGood = { ...minimalBlueprint([]), [key]: sample.good };
      const applied = JSON.parse(serializeAuthoredServiceBlueprint(coerceParsedAuthoredServiceBlueprint(withGood)));
      check(`top-level "${key}" survives a Definition-tab apply and save`,
        key in applied && JSON.stringify(applied[key]) !== undefined, JSON.stringify(applied));
      if (typeof sample.good !== 'object') {
        check(`top-level "${key}" keeps its value through apply and save`, applied[key] === sample.good, JSON.stringify(applied[key]));
      }
      const withBad = { ...minimalBlueprint([]), [key]: sample.bad };
      const issues = lintAuthoredServiceBlueprintDocument(withBad, JSON.stringify(withBad), CATALOG);
      check(`top-level "${key}" with the wrong type is reported, not silently dropped`,
        issues.some(issue => issue.pathHint === key), JSON.stringify(issues));
      const withGoodIssues = lintAuthoredServiceBlueprintDocument(withGood, JSON.stringify(withGood), CATALOG);
      check(`top-level "${key}" with the right type raises no issue about it`,
        !withGoodIssues.some(issue => issue.pathHint === key), JSON.stringify(withGoodIssues));
    }

    const typo = { ...minimalBlueprint([]), allowManualRestat: true };
    check('an unrecognised top-level property is reported, since saving would discard it',
      lintAuthoredServiceBlueprintDocument(typo, JSON.stringify(typo), CATALOG).some(issue => issue.pathHint === 'allowManualRestat'));
  }

  // Drift guard against the C# model: every property on ServiceBlueprint must be owned by the
  // editor (EDITOR_TOP_LEVEL_FIELDS). Without this, a new field is silently dropped by every editor
  // save until someone notices.
  {
    const source = csharpServiceBlueprintSource;
    const start = source.indexOf('public record ServiceBlueprint\n');
    const end = source.indexOf('\npublic ', start + 10);
    const body = source.slice(start, end < 0 ? undefined : end);
    const serverProperties = [...body.matchAll(/^\s{4}public [^\n(]*? (\w+)\s*(?:\{ get;|\r?\n\s{4}\{)/gm)]
      .map(match => match[1][0].toLowerCase() + match[1].slice(1));
    check('the drift guard found the C# ServiceBlueprint properties', serverProperties.includes('allowManualRestart'), JSON.stringify(serverProperties));

    for (const property of serverProperties) {
      check(`C# ServiceBlueprint.${property} is owned by the editor`,
        property in EDITOR_TOP_LEVEL_FIELDS);
    }
    // parameterSchemas has no C# property, so the server drops it when it deserialises a save.
    const EDITOR_ONLY = ['parameterSchemas'];
    for (const key of Object.keys(EDITOR_TOP_LEVEL_FIELDS)) {
      check(`editor-owned "${key}" exists on the C# ServiceBlueprint (or is consciously editor-only)`,
        serverProperties.includes(key) || EDITOR_ONLY.includes(key));
    }
  }

  // ── nested types: every C# property on a queue, stage, gateway or route survives apply + save ──
  // Reads the property names from the C# records, so a property added there with no editor handling
  // (or no sample here) fails the suite. A queue's assignmentPolicy/owningTeamId were silently
  // stripped by every editor save, turning a team-tray queue into an unassigned one.
  {
    const csharpProperties = (record: string): string[] => {
      const start = csharpServiceBlueprintSource.indexOf(`public record ${record}\n`);
      const end = csharpServiceBlueprintSource.indexOf('\n}\n', start);
      const body = csharpServiceBlueprintSource.slice(start, end);
      return [...body.matchAll(/^\s{4}public [^\n(]*? (\w+)\s*(?:\{ get;|\r?\n\s{4}\{)/gm)]
        .map(match => match[1][0].toLowerCase() + match[1].slice(1));
    };

    const route = { id: 'r1', target: 'next', trigger: 'go', label: 'L', style: 'primary', requiresRole: 'reviewer', showWhen: 'a == 1', actions: [{ type: 'forms.submit', timing: 'OnTransition' }] };
    const SAMPLES: Record<string, Record<string, unknown>> = {
      QueueDefinition: {
        key: 'q', displayName: 'Q', description: 'd', actor: 'caseworker', roleGates: ['g'],
        assignmentPolicy: 'team-tray', owningTeamId: 'team-a', tags: { k: 'v' },
      },
      StageDefinition: {
        stageKey: 'only', displayName: 'Only', description: 'd', stageType: 'Question', actor: 'a', queueKey: 'q',
        roleGates: ['g'], actions: [{ type: 'forms.submit', timing: 'OnEntry' }], components: [{ type: 'text', fieldKey: 'f', label: 'F' }],
        routes: [route], validations: [{ code: 'c', rule: 'true', message: 'm' }], icon: 'i',
      },
      ServiceBlueprintGatewayDefinition: {
        key: 'gw', displayName: 'G', description: 'd', gatewayType: 'Join', queueKey: 'q', actor: 'a', roleGates: ['g'],
        routes: [route], waitingContent: 'w', waitingExpectedSeconds: 5, waitingPollIntervalMs: 1000, waitingAllowDefer: true,
        waitingDeferMessage: 'later', requiredIncomingQueues: ['q'], icon: 'i',
      },
      ServiceBlueprintRouteDefinition: route,
    };
    const sorted = (value: unknown): unknown =>
      Array.isArray(value) ? value.map(sorted)
        : value && typeof value === 'object'
          ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sorted((value as Record<string, unknown>)[key])]))
          : value;
    const embed = (record: string, value: Record<string, unknown>): Record<string, unknown> => {
      const blueprint: Record<string, unknown> = { ...minimalBlueprint([]) };
      if (record === 'QueueDefinition') blueprint.queues = [value];
      else if (record === 'StageDefinition') blueprint.stages = [value];
      else if (record === 'ServiceBlueprintGatewayDefinition') blueprint.gateways = [value];
      else (blueprint.stages as Record<string, unknown>[])[0].routes = [value];
      return blueprint;
    };
    const extract = (record: string, saved: Record<string, any>): Record<string, unknown> => {
      if (record === 'QueueDefinition') return saved.queues[0];
      if (record === 'StageDefinition') return saved.stages[0];
      if (record === 'ServiceBlueprintGatewayDefinition') return saved.gateways[0];
      return saved.stages[0].routes[0];
    };

    for (const record of Object.keys(SAMPLES)) {
      const sample = SAMPLES[record];
      const properties = csharpProperties(record);
      check(`the drift guard found the C# ${record} properties`, properties.length > 3, JSON.stringify(properties));
      const saved = JSON.parse(serializeAuthoredServiceBlueprint(coerceParsedAuthoredServiceBlueprint(embed(record, sample))));
      const out = extract(record, saved);
      for (const property of properties) {
        check(`C# ${record}.${property} has an editor test sample (add one when the model gains a property)`, property in sample);
        check(`C# ${record}.${property} survives an editor apply and save`,
          property in out && JSON.stringify(sorted(out[property])) === JSON.stringify(sorted(sample[property])),
          `${property}: expected ${JSON.stringify(sample[property])}, saved ${JSON.stringify(out[property])}`);
      }
    }
  }

  {
    const userBlueprint = (defaultFrom: string) => ({
      definitionKey: 'fixture', displayName: 'Fixture', initialStage: 'only', queues: [], gateways: [],
      calculations: { fields: { user: { source: 'service', shape: { name: { valueKind: 'string' } } } } },
      stages: [{
        stageKey: 'only', displayName: 'Only', queueKey: 'citizen', stageType: 'Question',
        components: [{ type: 'text', fieldKey: 'name', label: 'Name', defaultFrom }],
      }],
    });
    const lint = (defaultFrom: string) => {
      const parsed = userBlueprint(defaultFrom);
      return lintAuthoredServiceBlueprintDocument(parsed, JSON.stringify(parsed), CATALOG);
    };
    check('lint: a dotted defaultFrom into a declared service-field shape is accepted',
      !lint('user.name').some(issue => issue.pathHint?.includes('defaultFrom')),
      JSON.stringify(lint('user.name')));
    check('lint: a dotted defaultFrom naming a property the shape does not declare is flagged',
      lint('user.nmae').some(issue => issue.pathHint?.includes('defaultFrom') && issue.message.includes('"nmae"')),
      JSON.stringify(lint('user.nmae')));
    check('lint: a dotted defaultFrom on an undeclared root is flagged',
      lint('person.name').some(issue => issue.pathHint?.includes('defaultFrom')),
      JSON.stringify(lint('person.name')));
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
