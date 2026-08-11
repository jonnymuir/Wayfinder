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
 * appended after everything it could reference. Reference discovery is inline CodeMirror
 * autocomplete (calculation-expression-editor-codemirror.ts), not a separate picker widget —
 * the full, real interaction (typing, the completion tooltip opening, accepting a suggestion) is
 * verified end to end by Wayfinder.ReferenceApp.Tests/tests/calculations-editor.spec.ts, which
 * drives an actual browser via Playwright directly. testing-library's synthetic typing (this
 * harness's own interaction layer) doesn't reliably drive a shadow-DOM-hosted CodeMirror
 * contenteditable, so what's meaningful to verify at this layer is the wiring this story exists
 * to cover: the new row's expression editor receives a real, non-empty completions list.
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

    const exprEditor = newRow!.querySelector('wayfinder-calculation-expression-editor') as HTMLElement & {
      completions: Array<{ name: string; detail: string }>;
    };
    await expect(exprEditor).not.toBeNull();
    await expect(exprEditor.completions.some(item => item.name === 'age')).toBe(true);
  },
};
