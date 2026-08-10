import type { Meta, StoryObj } from '@storybook/web-components';
import { expect } from '@storybook/test';
import './wayfinder-calculations-editor.js';
import type { WayfinderCalculationsEditorElement } from './wayfinder-calculations-editor.js';
import type { AuthoredServiceBlueprint, ComponentDescriptor } from './types.js';

const CATALOG: ComponentDescriptor[] = [
  {
    discriminator: 'number',
    displayName: 'Number input',
    category: 'Input',
    clrType: 'NumberInputComponent',
    isInput: true,
    properties: [],
    containment: { kind: 'None' },
  },
];

function fixtureBlueprint(): AuthoredServiceBlueprint {
  return {
    definitionKey: 'calc-fixture',
    displayName: 'Calculations fixture',
    version: 1,
    initialStage: 'only',
    requestPolicy: 'single',
    stages: [
      {
        stateKey: 'only',
        displayName: 'Only',
        queueKey: 'citizen',
        components: [
          { type: 'number', fieldKey: 'age', label: 'Age', required: true, default: '30' } as never,
        ],
      },
    ],
    calculations: {
      fields: {
        doubledAge: { expr: 'age * 2' },
        tripledAge: { expr: 'age * 3' },
      },
      tables: {
        ageFactor: { interpolate: 'linear', values: { '20': 0.5, '60': 1.5 } },
      },
      series: {
        agesAhead: {
          over: 'yearsAhead',
          from: '0',
          to: '5',
          values: { projectedAge: 'age + yearsAhead' },
        },
      },
    },
  };
}

function makeElement(): WayfinderCalculationsEditorElement {
  const el = document.createElement('wayfinder-calculations-editor') as WayfinderCalculationsEditorElement;
  el.serviceBlueprint = fixtureBlueprint();
  el.componentCatalog = CATALOG;
  el.addEventListener('service-blueprint-updated', event => {
    const detail = (event as CustomEvent<{ serviceBlueprint: AuthoredServiceBlueprint }>).detail;
    el.serviceBlueprint = detail.serviceBlueprint;
  });
  el.style.cssText = 'display:block;width:640px;height:720px;';
  return el;
}

const meta: Meta = {
  title: 'ServiceBlueprintEditor/CalculationsEditor',
  render: () => makeElement(),
};

export default meta;
type Story = StoryObj;

/**
 * Proves the core round-trip: two real fields render with their real expressions, each shows a
 * live computed value (age=30 default -> doubledAge=60, tripledAge=90), and a table/series both
 * render too.
 */
export const Default: Story = {
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 150));
    const el = canvasElement.querySelector('wayfinder-calculations-editor') as WayfinderCalculationsEditorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const fieldRows = root.querySelectorAll('[data-wayfinder-calc-field]');
    await expect(fieldRows.length).toBe(2);

    await new Promise(resolve => setTimeout(resolve, 200));
    const previews = root.querySelectorAll('[data-wayfinder-calc-field-preview]');
    const previewTexts = Array.from(previews).map(node => node.textContent?.trim());
    await expect(previewTexts).toContain('= 60');
    await expect(previewTexts).toContain('= 90');

    const tableRow = root.querySelector('[data-wayfinder-calc-table="ageFactor"]');
    await expect(tableRow).not.toBeNull();

    const seriesRow = root.querySelector('[data-wayfinder-calc-series="agesAhead"]');
    await expect(seriesRow).not.toBeNull();
  },
};

/**
 * Adding a field that depends on an existing one — no reordering needed since the new field is
 * appended after everything it could reference — then using "insert a reference" to add a real
 * field name into its expression without having to remember the exact spelling.
 */
export const AddFieldAndInsertReference: Story = {
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 150));
    const el = canvasElement.querySelector('wayfinder-calculations-editor') as WayfinderCalculationsEditorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const addFieldButton = Array.from(root.querySelectorAll('button')).find(button => button.textContent?.includes('+ Add field'));
    await expect(addFieldButton).not.toBeUndefined();
    addFieldButton!.click();
    await el.updateComplete;

    const fieldRows = root.querySelectorAll('[data-wayfinder-calc-field]');
    await expect(fieldRows.length).toBe(3);

    const newRow = root.querySelector('[data-wayfinder-calc-field="field1"]');
    await expect(newRow).not.toBeNull();

    // The new field's expression editor loads CodeMirror via a dynamic import — give it a
    // moment to finish mounting before insert-a-reference (which needs the real CM6 view) can
    // do anything.
    await new Promise(resolve => setTimeout(resolve, 300));

    const insertInput = newRow!.querySelector('.reference-picker-input') as HTMLInputElement;
    await expect(insertInput).not.toBeNull();
    const datalistId = insertInput.getAttribute('list')!;
    const datalist = newRow!.querySelector(`#${datalistId}`) as HTMLDataListElement;
    const optionValues = Array.from(datalist.options).map(option => option.value);
    await expect(optionValues.some(value => value.includes('(age)'))).toBe(true);

    const ageOption = optionValues.find(value => value.includes('(age)'))!;
    insertInput.value = ageOption;
    insertInput.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;
    await new Promise(resolve => setTimeout(resolve, 100));

    const calcFields = el.serviceBlueprint!.calculations!.fields as Record<string, { expr?: string }>;
    await expect(calcFields.field1.expr).toBe('age');
  },
};
