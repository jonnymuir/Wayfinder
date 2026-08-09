import type { AuthoredComponent, AuthoredServiceBlueprint, ComponentDescriptor } from './types.js';
import { buildPropertyReferenceContext, collectStageInputFields } from './component-property-references.js';

const CATALOG: ComponentDescriptor[] = [
  {
    discriminator: 'text',
    displayName: 'Text input',
    category: 'Input',
    clrType: 'TextInputComponent',
    isInput: true,
    properties: [],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'radio',
    displayName: 'Radios',
    category: 'Input',
    clrType: 'RadiosComponent',
    isInput: true,
    properties: [],
    containment: { kind: 'KeyedChildren', propertyName: 'conditionalChildren', keySourceProperty: 'options' },
  },
  {
    discriminator: 'fieldset',
    displayName: 'Fieldset',
    category: 'Container',
    clrType: 'FieldsetComponent',
    isInput: false,
    properties: [],
    containment: { kind: 'ChildList', propertyName: 'children' },
  },
  {
    discriminator: 'accordion',
    displayName: 'Accordion',
    category: 'Container',
    clrType: 'AccordionComponent',
    isInput: false,
    properties: [],
    containment: { kind: 'NamedSections', propertyName: 'sections', sectionChildrenPropertyName: 'children' },
  },
  {
    discriminator: 'heading',
    displayName: 'Heading',
    category: 'Content',
    clrType: 'HeadingComponent',
    isInput: false,
    properties: [],
    containment: { kind: 'None' },
  },
];

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

  // ── collectStageInputFields ──────────────────────────────────────────────
  {
    const components = [
      { type: 'text', fieldKey: 'name', label: 'Full name' },
      { type: 'heading', content: 'Section heading' },
    ] as unknown as AuthoredComponent[];
    const fields = collectStageInputFields(components, CATALOG);
    check('top-level input fields are collected', fields.length === 1 && fields[0].fieldKey === 'name');
    check('content-only components are excluded', !fields.some(f => f.fieldKey === undefined));
  }

  {
    const components = [
      {
        type: 'fieldset',
        legend: 'Group',
        children: [{ type: 'text', fieldKey: 'nested', label: 'Nested field' }],
      },
    ] as unknown as AuthoredComponent[];
    const fields = collectStageInputFields(components, CATALOG);
    check('fields nested inside a ChildList container are found', fields.some(f => f.fieldKey === 'nested'));
  }

  {
    const components = [
      {
        type: 'accordion',
        sections: [
          { heading: 'One', children: [{ type: 'text', fieldKey: 'inSection', label: 'In section' }] },
        ],
      },
    ] as unknown as AuthoredComponent[];
    const fields = collectStageInputFields(components, CATALOG);
    check('fields nested inside a NamedSections container are found', fields.some(f => f.fieldKey === 'inSection'));
  }

  {
    const components = [
      {
        type: 'radio',
        fieldKey: 'choice',
        label: 'Choice',
        options: ['yes', 'no'],
        conditionalChildren: {
          yes: [{ type: 'text', fieldKey: 'why', label: 'Why' }],
        },
      },
    ] as unknown as AuthoredComponent[];
    const fields = collectStageInputFields(components, CATALOG);
    check('the radio itself is collected with its options',
      fields.some(f => f.fieldKey === 'choice' && JSON.stringify(f.options) === JSON.stringify(['yes', 'no'])));
    check('fields nested inside KeyedChildren are found', fields.some(f => f.fieldKey === 'why'));
  }

  {
    const fields = collectStageInputFields(undefined, CATALOG);
    check('undefined components list returns an empty array', fields.length === 0);
  }

  // ── buildPropertyReferenceContext ────────────────────────────────────────
  {
    const blueprint = {
      stages: [
        { stateKey: 'first', displayName: 'First stage', components: [{ type: 'text', fieldKey: 'a', label: 'A' }] },
        { stateKey: 'second', displayName: 'Second stage', components: [{ type: 'text', fieldKey: 'b', label: 'B' }] },
      ],
      calculations: { fields: { premium: { expr: '1' }, excess: { expr: '2' } } },
    } as unknown as AuthoredServiceBlueprint;

    const context = buildPropertyReferenceContext(blueprint, [{ type: 'text', fieldKey: 'a', label: 'A' }] as unknown as AuthoredComponent[], CATALOG);

    check('stageOptions lists every stage', context.stageOptions.length === 2);
    check('stageOptions labels combine displayName and key',
      context.stageOptions[0].label === 'First stage (first)', context.stageOptions[0].label);
    check('calculationFieldNames lists every calculation field name',
      JSON.stringify(context.calculationFieldNames.sort()) === JSON.stringify(['excess', 'premium']));
    check('siblingFields reflects the passed-in stage components only', context.siblingFields.length === 1);
    check('allFields spans every stage in the blueprint, not just the passed-in one',
      context.allFields.length === 2, JSON.stringify(context.allFields));
  }

  {
    const context = buildPropertyReferenceContext(null, undefined, CATALOG);
    check('a null blueprint yields empty stage/calculation/field lists',
      context.stageOptions.length === 0 && context.calculationFieldNames.length === 0 && context.allFields.length === 0);
  }

  return failures;
}
