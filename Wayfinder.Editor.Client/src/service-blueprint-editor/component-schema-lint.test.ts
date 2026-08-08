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
    containment: { kind: 'ChildList', propertyName: 'Children' },
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
    containment: { kind: 'KeyedChildren', propertyName: 'ConditionalChildren', keySourceProperty: 'Options' },
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

  if (failures > 0) {
    console.error(`\n${failures} component schema/lint check(s) failed.`);
  } else {
    console.log('\nAll component schema/lint checks passed.');
  }
  return failures;
}
