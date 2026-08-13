import type { Meta, StoryObj } from '@storybook/web-components';
import { expect } from '@storybook/test';
import './wayfinder-step-inspector.js';
import type { WayfinderStepInspectorElement } from './wayfinder-step-inspector.js';
import { STUB_ACTION_CATALOG, STUB_SERVICE_BLUEPRINT } from './types.js';
import type { ActionCatalogEntry, AuthoredServiceBlueprint, ComponentDescriptor, SupportSystemDescriptor } from './types.js';

type StoryArgs = {
  serviceBlueprint: AuthoredServiceBlueprint | null;
  selectedStageKey: string | null;
  selectedGatewayKey?: string | null;
  actionCatalog: ActionCatalogEntry[];
  componentCatalog: ComponentDescriptor[];
  supportSystemCatalog?: SupportSystemDescriptor[];
};

function makeElement(args: StoryArgs): WayfinderStepInspectorElement {
  const el = document.createElement('wayfinder-step-inspector') as WayfinderStepInspectorElement;
  el.serviceBlueprint = args.serviceBlueprint;
  el.selectedStageKey = args.selectedStageKey;
  el.selectedGatewayKey = args.selectedGatewayKey ?? null;
  el.actionCatalog = args.actionCatalog;
  el.componentCatalog = args.componentCatalog ?? [];
  el.supportSystemCatalog = args.supportSystemCatalog ?? [];
  el.addEventListener('service-blueprint-updated', event => {
    const detail = (event as CustomEvent<{
      serviceBlueprint: AuthoredServiceBlueprint;
      selection?: { kind?: 'stage' | 'gateway'; stageKey?: string; gatewayKey?: string } | null;
    }>).detail;
    el.serviceBlueprint = detail.serviceBlueprint;
    if (detail.selection?.kind === 'gateway') {
      el.selectedGatewayKey = detail.selection.gatewayKey ?? null;
      el.selectedStageKey = null;
    } else if (detail.selection?.stageKey) {
      el.selectedStageKey = detail.selection.stageKey;
      el.selectedGatewayKey = null;
    } else {
      el.selectedStageKey = null;
      el.selectedGatewayKey = null;
    }
  });
  el.style.cssText = 'display:block;width:380px;height:640px;';
  return el;
}

const meta: Meta<StoryArgs> = {
  title: 'Service Blueprint Editor/Step Inspector',
  component: 'wayfinder-step-inspector',
  tags: ['autodocs'],
  parameters: {
    a11y: {
      config: {
        rules: [
          { id: 'color-contrast', enabled: true },
          { id: 'heading-order', enabled: true },
        ],
      },
    },
  },
  args: {
    serviceBlueprint: null,
    selectedStageKey: null,
    selectedGatewayKey: null,
    actionCatalog: STUB_ACTION_CATALOG,
    componentCatalog: [],
    supportSystemCatalog: [],
  },
  render: args => makeElement(args),
};

export default meta;
type Story = StoryObj<StoryArgs>;

export const Empty: Story = {
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 100));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;
    await expect(el.shadowRoot?.querySelector('.empty-state')).not.toBeNull();
  },
};

export const EditableStage: Story = {
  args: {
    serviceBlueprint: STUB_SERVICE_BLUEPRINT,
    selectedStageKey: 'reviewer-assessment',
  },
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 120));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;

    const root = el.shadowRoot!;
    const title = root.querySelector<HTMLInputElement>('[data-wayfinder-stage-title]')!;
    title.value = 'Applicant Intake';
    title.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;

    const lane = root.querySelector<HTMLInputElement>('[data-wayfinder-stage-queue]')!;
    lane.value = 'member';
    lane.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;

    const stageType = root.querySelector<HTMLSelectElement>('[data-wayfinder-stage-type]')!;
    stageType.value = 'review';
    stageType.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;

    const actionEditor = root.querySelector('wayfinder-stage-action-editor')!;
    await expect(actionEditor).not.toBeNull();
    await expect(actionEditor.shadowRoot?.querySelectorAll('[data-wayfinder-stage-action]').length).toBe(2);
    await expect(actionEditor.shadowRoot?.querySelector('[data-wayfinder-action-forms-editor="1"]')).not.toBeNull();
    await expect(root.querySelector('[data-wayfinder-stage-detail="reviewer-assessment"]')).not.toBeNull();
  },
};

export const ActionConfiguration: Story = {
  args: {
    serviceBlueprint: STUB_SERVICE_BLUEPRINT,
    selectedStageKey: 'reviewer-assessment',
  },
};

// A small gateway-shaped serviceBlueprint so the inspector can render the new
// outgoing-routes section with a single route whose action editor mirrors
// the previous transition-action picker scope.
const GATEWAY_ROUTE_SERVICE_BLUEPRINT = {
  definitionKey: 'gateway-route-action-fixture',
  displayName: 'Gateway route action fixture',
  version: 1,
  requestPolicy: 'single',
  initialStage: 'submitted',
  stages: [
    {
      stateKey: 'submitted',
      displayName: 'Submitted',
      kind: 'Question',
      actor: 'public',
      actions: [],
      components: [],
      roleGates: [],
      routes: [{ id: 'submitted--route--review-split', target: 'review-split', trigger: 'route' }],
    },
    {
      stateKey: 'reviewer-assessment',
      displayName: 'Reviewer assessment',
      kind: 'Question',
      actor: 'reviewer',
      actions: [],
      components: [],
      roleGates: ['reviewer'],
    },
    {
      stateKey: 'applicant-amendments',
      displayName: 'Applicant amendments',
      kind: 'Question',
      actor: 'public',
      actions: [],
      components: [],
      roleGates: [],
    },
  ],
  transitions: [
    { fromState: 'submitted', toState: 'review-split', action: 'route' },
    { fromState: 'review-split', toState: 'reviewer-assessment', action: 'route for review', requiresRole: 'reviewer', metadata: { actions: [{ type: 'forms.submit', timing: 'OnTransition' }] } },
  ],
  metadata: { gateways: [
    {
      key: 'review-split',
      displayName: 'Review split',
      gatewayType: 'Split',
      queueKey: 'public',
      actor: 'public',
      source: 'submitted',
      roleGates: [],
      routes: [
        {
          id: 'submitted--route-for-review--reviewer-assessment',
          target: 'reviewer-assessment',
          trigger: 'route for review',
          requiresRole: 'reviewer',
          actions: [
            {
              type: 'forms.submit',
              timing: 'OnTransition',
            },
          ],
        },
      ],
    },
  ] },
} as unknown as AuthoredServiceBlueprint;

export const TransitionSelected: Story = {
  // Slice 3b.1 removed transition-only selection. Route editing now lives in
  // the gateway inspector — this story mounts a split gateway with two routes
  // so the editor-host gateway-route specs have a backing fixture.
  args: {
    serviceBlueprint: GATEWAY_ROUTE_SERVICE_BLUEPRINT,
    selectedStageKey: null,
    selectedGatewayKey: 'review-split',
  },
};

export const TransitionActionConfiguration: Story = {
  // The previous transition-action picker filter check now runs against a
  // route action editor mounted inside the gateway inspector's outgoing
  // routes panel. The action-editor data attributes (data-wayfinder-action-*)
  // are identical so existing tests keep working.
  args: {
    serviceBlueprint: GATEWAY_ROUTE_SERVICE_BLUEPRINT,
    selectedStageKey: null,
    selectedGatewayKey: 'review-split',
  },
};

// Stage with no gateway — "+ Add route" button must be visible and
// must create the gateway on click. The `applicant-amendments` stage in
// GATEWAY_ROUTE_SERVICE_BLUEPRINT has no Split gateway attached.
export const AddRouteNoGateway: Story = {
  args: {
    serviceBlueprint: GATEWAY_ROUTE_SERVICE_BLUEPRINT,
    selectedStageKey: 'applicant-amendments',
    selectedGatewayKey: null,
  },
};

// Gateway that already has one route — "+ Add route" button must be
// visible and must append a second route on click.
export const AddRouteExistingGateway: Story = {
  args: {
    serviceBlueprint: GATEWAY_ROUTE_SERVICE_BLUEPRINT,
    selectedStageKey: null,
    selectedGatewayKey: 'review-split',
  },
};

// A small fixture catalog — not the full 27-type built-in catalog (that's fetched live from a
// real host in production, see component-catalog.ts), just enough shapes to exercise every real
// code path in the property editor: a flat Input type ('text'), a flat Content type with a
// textarea editor ('body'), a type with a genuinely recursive Array-of-Object property
// ('stat-group', mirroring StatGroupComponent.Items), and a Container type to prove its own flat
// properties are still editable while its children stay JSON-editor-only ('fieldset').
const COMPONENT_CATALOG_FIXTURE: ComponentDescriptor[] = [
  {
    discriminator: 'text',
    displayName: 'Text input',
    category: 'Input',
    clrType: 'TextInputComponent',
    isInput: true,
    // Keys/propertyName below are camelCase, matching what a live host actually sends: the
    // server holds the real CLR property name internally (e.g. "FieldKey", via nameof() in
    // BuiltInComponentDescriptors.cs — needed for reflection and compile-time rename-safety) but
    // converts it to camelCase at the JSON boundary (ComponentDescriptor.cs's
    // PropertyNameJsonConverter) specifically so neither this fixture nor the editor code that
    // consumes it ever has to think about the C#-internal casing.
    properties: [
      { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
      { key: 'label', title: 'Label', valueKind: 'String', required: true },
      { key: 'required', title: 'Required', valueKind: 'Boolean', required: false, editor: 'toggle' },
      { key: 'conditionalOn', title: 'Conditional on field', valueKind: 'String', required: false, format: 'field-ref' },
      { key: 'pattern', title: 'Pattern (regex)', valueKind: 'String', required: false, format: 'pattern' },
    ],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'body',
    displayName: 'Body text',
    category: 'Content',
    clrType: 'BodyComponent',
    isInput: false,
    properties: [
      { key: 'content', title: 'Content', valueKind: 'String', required: true, editor: 'textarea' },
    ],
    containment: { kind: 'None' },
  },
  {
    discriminator: 'stat-group',
    displayName: 'Statistic group',
    category: 'DataDisplay',
    clrType: 'StatGroupComponent',
    isInput: false,
    properties: [
      { key: 'title', title: 'Title', valueKind: 'String', required: false },
      {
        key: 'items',
        title: 'Statistic tiles',
        valueKind: 'Array',
        required: true,
        items: {
          key: 'item',
          title: 'Statistic tile',
          valueKind: 'Object',
          required: false,
          properties: [
            { key: 'label', title: 'Label', valueKind: 'String', required: true },
            { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
          ],
        },
      },
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
];

// Proves the schema-driven component add/edit UI (phase 6a of the component-catalog
// extensibility work) genuinely works: add a flat component, edit its scalar property, add a
// component with a recursive Array-of-Object property and edit a nested item field, then delete
// one — all through real DOM events against native form controls (no custom widgets in this
// slice, so keyboard operability comes for free from the browser).
export const ComponentAddEditDelete: Story = {
  args: {
    serviceBlueprint: STUB_SERVICE_BLUEPRINT,
    selectedStageKey: 'reviewer-assessment',
    componentCatalog: COMPONENT_CATALOG_FIXTURE,
  },
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 120));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const typeSelect = root.querySelector<HTMLSelectElement>('[data-wayfinder-add-component-type]')!;
    const addButton = root.querySelector<HTMLButtonElement>('.component-add-row .secondary-button')!;
    await expect(typeSelect).not.toBeNull();
    await expect(addButton).not.toBeNull();

    // Add a "Body text" component — it auto-expands for editing.
    typeSelect.value = 'body';
    addButton.click();
    await el.updateComplete;

    let stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    await expect(stage.components?.length).toBe(1);
    await expect(stage.components?.[0].type).toBe('body');

    const contentField = root.querySelector<HTMLTextAreaElement>('[data-wayfinder-component-index="0"] textarea')!;
    await expect(contentField).not.toBeNull();
    contentField.value = 'Hello from the properties panel.';
    contentField.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await el.updateComplete;

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    await expect((stage.components?.[0] as { content?: string }).content).toBe('Hello from the properties panel.');

    // Add a "Statistic group" and exercise the recursive Array-of-Object property editor.
    typeSelect.value = 'stat-group';
    addButton.click();
    await el.updateComplete;

    const statGroupItem = root.querySelector<HTMLElement>('[data-wayfinder-component-index="1"]')!;
    await expect(statGroupItem).not.toBeNull();
    const addTileButton = Array.from(statGroupItem.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.includes('Add'))!;
    await expect(addTileButton).not.toBeUndefined();
    addTileButton.click();
    await el.updateComplete;

    const tileLabelField = statGroupItem.querySelector<HTMLInputElement>('.property-array-item input');
    await expect(tileLabelField).not.toBeNull();
    tileLabelField!.value = 'Total';
    tileLabelField!.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await el.updateComplete;

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    const statGroup = stage.components?.[1] as { items?: Array<{ label?: string }> };
    await expect(statGroup.items?.[0]?.label).toBe('Total');

    // Delete the body component — the stat-group survives.
    const deleteButtons = root.querySelectorAll<HTMLButtonElement>('.component-item-actions .danger-button');
    await expect(deleteButtons.length).toBe(2);
    deleteButtons[0].click();
    await el.updateComplete;

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    await expect(stage.components?.length).toBe(1);
    await expect(stage.components?.[0].type).toBe('stat-group');
  },
};

// The "reference-aware" property fields (2026-08-09): a property tagged with a Format like
// `field-ref`/`pattern` renders a live `<select>`/preset-and-tester instead of a blank text box —
// see component-property-references.ts and component-property-editor.ts's `referenceSelectOptions`/
// `renderPatternField`. Proves: (1) a second field's "Conditional on field" dropdown is populated
// with the first field's real fieldKey, not free text, and a selection reaches the real saved
// component; (2) inserting a regex preset writes the real pattern string.
export const ComponentReferenceAwareFields: Story = {
  args: {
    serviceBlueprint: STUB_SERVICE_BLUEPRINT,
    selectedStageKey: 'reviewer-assessment',
    componentCatalog: COMPONENT_CATALOG_FIXTURE,
  },
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 120));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const typeSelect = root.querySelector<HTMLSelectElement>('[data-wayfinder-add-component-type]')!;
    const addButton = root.querySelector<HTMLButtonElement>('.component-add-row .secondary-button')!;

    // First field — no siblings yet, so its own "Conditional on field" dropdown has nothing to
    // offer beyond "-- Not set --".
    typeSelect.value = 'text';
    addButton.click();
    await el.updateComplete;
    const firstFieldKey = root.querySelector<HTMLInputElement>('[data-wayfinder-component-index="0"] .component-editor input')!;
    firstFieldKey.value = 'firstName';
    firstFieldKey.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await el.updateComplete;

    // Second field — its "Conditional on field" dropdown must now list "firstName" as a real
    // option, not a blank text input.
    typeSelect.value = 'text';
    addButton.click();
    await el.updateComplete;

    const secondItem = root.querySelector<HTMLElement>('[data-wayfinder-component-index="1"]')!;
    const conditionalOnSelect = Array.from(secondItem.querySelectorAll<HTMLSelectElement>('.component-editor select'))
      .find(select => select.id.endsWith('-conditionalOn'))!;
    await expect(conditionalOnSelect).not.toBeUndefined();
    const optionValues = Array.from(conditionalOnSelect.options).map(option => option.value);
    await expect(optionValues).toContain('firstName');

    conditionalOnSelect.value = 'firstName';
    conditionalOnSelect.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;

    let stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    await expect((stage.components?.[1] as { conditionalOn?: string }).conditionalOn).toBe('firstName');

    // Insert the "Letters only" regex preset into the second field's Pattern property.
    const presetSelect = Array.from(secondItem.querySelectorAll<HTMLSelectElement>('.component-editor select'))
      .find(select => select.id.endsWith('-pattern-preset'))!;
    await expect(presetSelect).not.toBeUndefined();
    const lettersOnlyOption = Array.from(presetSelect.options).find(option => option.textContent === 'Letters only')!;
    presetSelect.value = lettersOnlyOption.value;
    presetSelect.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    await expect((stage.components?.[1] as { pattern?: string }).pattern).toBe('^[A-Za-z]+$');
  },
};

// Phase 6b: a container type's (fieldset's) own flat properties AND its actual child
// components are both genuinely editable through this UI — add a fieldset, add a text child
// inside it, edit both the fieldset's own legend and the child's label, confirm both round-trip
// into the right nested position in the real data (children[0].label, not some sibling slot).
export const ComponentRecursiveChildEditing: Story = {
  args: {
    serviceBlueprint: STUB_SERVICE_BLUEPRINT,
    selectedStageKey: 'reviewer-assessment',
    componentCatalog: COMPONENT_CATALOG_FIXTURE,
  },
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 120));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const typeSelect = root.querySelector<HTMLSelectElement>('[data-wayfinder-add-component-type]')!;
    const addButton = root.querySelector<HTMLButtonElement>('.component-add-row .secondary-button')!;
    typeSelect.value = 'fieldset';
    addButton.click();
    await el.updateComplete;

    const componentItem = root.querySelector<HTMLElement>('[data-wayfinder-component-index="0"]')!;
    await expect(componentItem).not.toBeNull();

    // The fieldset's own "legend" property field is still there, alongside its children UI —
    // the only <input> at this point, before any child has been added.
    const legendField = componentItem.querySelector<HTMLInputElement>('.component-editor input')!;
    await expect(legendField).not.toBeNull();
    legendField.value = 'Applicant details';
    legendField.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await el.updateComplete;

    const childContainer = componentItem.querySelector<HTMLElement>('.child-container')!;
    await expect(childContainer).not.toBeNull();
    const childTypeSelect = childContainer.querySelector<HTMLSelectElement>('[data-wayfinder-add-child-type]')!;
    const childAddButton = childContainer.querySelector<HTMLButtonElement>('.component-add-row .secondary-button')!;
    childTypeSelect.value = 'text';
    childAddButton.click();
    await el.updateComplete;

    let stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    let fieldset = stage.components?.[0] as { legend?: string; children?: Array<{ type: string; label?: string }> };
    await expect(fieldset.legend).toBe('Applicant details');
    await expect(fieldset.children?.length).toBe(1);
    await expect(fieldset.children?.[0].type).toBe('text');

    // Expand the newly-added child (a native <details>, so a plain click on its <summary> is
    // real keyboard-equivalent activation — Enter/Space on a focused summary does the same) and
    // edit its "label" field specifically (the child's own second declared property).
    const childDetails = componentItem.querySelector<HTMLDetailsElement>('.child-editor')!;
    childDetails.querySelector('summary')!.click();
    await el.updateComplete;

    const childInputs = childDetails.querySelectorAll<HTMLInputElement>('.component-editor input');
    await expect(childInputs.length).toBeGreaterThan(1);
    const childLabelField = childInputs[1]; // fieldKey, then label, per COMPONENT_CATALOG_FIXTURE's 'text' properties order
    childLabelField.value = 'Full name';
    childLabelField.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await el.updateComplete;

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    fieldset = stage.components?.[0] as { legend?: string; children?: Array<{ type: string; label?: string }> };
    await expect(fieldset.children?.[0].label).toBe('Full name');

    // Delete the child — the surviving parent's own "+ Add component" control (inside the same
    // child-list container) must receive focus, never <body>, per this file's own WCAG-risk
    // handling for a delete whose subtree contained the current focus.
    const deleteChildButton = childContainer.querySelector<HTMLButtonElement>('.component-item-actions .danger-button')!;
    deleteChildButton.click();
    await el.updateComplete;
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

    stage = el.serviceBlueprint!.stages.find(s => s.stateKey === 'reviewer-assessment')!;
    fieldset = stage.components?.[0] as { legend?: string; children?: Array<{ type: string; label?: string }> };
    await expect(fieldset.children?.length).toBe(0);
    await expect(root.activeElement?.closest('.component-add-row')).not.toBeNull();
  },
};

// A small fixture proving a support-system-call action's own dedicated editor — see
// docs/guides/support-systems.md and Wayfinder.ReferenceApp/service-blueprints/
// juggling-licence.json's real "insurer-validation" stage, which this mirrors in shape (fictional
// "SafetyNet Underwriting" support system, "validate-risk-assessment" capability). Two stages: one
// with real captured input fields a capability input can bind to (a support-system-call action's
// inputs are typically bound to a field captured earlier, not on the action's own stage — see
// supportSystemFieldReferences' own doc comment in wayfinder-step-inspector.ts), and the
// automation-queue stage carrying the action itself.
const SUPPORT_SYSTEM_CALL_SERVICE_BLUEPRINT = {
  definitionKey: 'support-system-call-fixture',
  displayName: 'Support system call fixture',
  version: 1,
  requestPolicy: 'single',
  initialStage: 'risk-assessment',
  stages: [
    {
      stateKey: 'risk-assessment',
      displayName: 'Risk assessment',
      kind: 'Question',
      actor: 'citizen',
      actions: [],
      roleGates: [],
      components: [
        {
          type: 'file-upload',
          fieldKey: 'riskAssessment',
          label: 'Risk assessment file',
          required: false,
        },
        {
          type: 'text',
          fieldKey: 'riskMitigationNotes',
          label: 'How are you mitigating the risk?',
          required: false,
        },
      ],
      routes: [{ id: 'risk-assessment--continue--insurer-validation', target: 'insurer-validation', trigger: 'continue' }],
    },
    {
      stateKey: 'insurer-validation',
      displayName: 'Insurer validation',
      kind: 'Question',
      actor: 'automation',
      roleGates: [],
      components: [],
      actions: [
        {
          type: 'support-system-call',
          timing: 'OnEntry',
          params: {
            supportSystemKey: 'safetynet-underwriting',
            capabilityKey: 'validate-risk-assessment',
            inputs: { File: 'riskAssessment', Notes: 'riskMitigationNotes' },
          },
          summary: 'Send the risk assessment to SafetyNet Underwriting.',
        },
      ],
      routes: [
        { id: 'insurer-validation--approved--done', target: 'done', trigger: 'approved' },
        { id: 'insurer-validation--rejected--done', target: 'done', trigger: 'rejected' },
      ],
    },
    {
      stateKey: 'done',
      displayName: 'Done',
      kind: 'Confirmation',
      actor: 'citizen',
      actions: [],
      components: [],
      roleGates: [],
    },
  ],
} as unknown as AuthoredServiceBlueprint;

// A minimal, story-local catalog (not COMPONENT_CATALOG_FIXTURE above — that one's shared by
// three other stories with their own assertions about its exact contents) — just enough for
// collectStageInputFields (component-property-references.ts) to recognise
// SUPPORT_SYSTEM_CALL_SERVICE_BLUEPRINT's two captured fields as real inputs, so the
// support-system-call action's own field-ref dropdowns have something to bind to.
const SUPPORT_SYSTEM_CALL_COMPONENT_CATALOG_FIXTURE: ComponentDescriptor[] = [
  {
    discriminator: 'file-upload',
    displayName: 'File upload',
    category: 'Input',
    clrType: 'FileUploadComponent',
    isInput: true,
    properties: [
      { key: 'fieldKey', title: 'Field key', valueKind: 'String', required: true },
      { key: 'label', title: 'Label', valueKind: 'String', required: true },
    ],
    containment: { kind: 'None' },
  },
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
];

const SUPPORT_SYSTEM_CATALOG_FIXTURE: SupportSystemDescriptor[] = [
  {
    key: 'safetynet-underwriting',
    displayName: 'SafetyNet Underwriting',
    description: "A fictional insurer that validates a juggling licence applicant's risk assessment.",
    capabilities: [
      {
        key: 'validate-risk-assessment',
        displayName: 'Validate a risk assessment',
        description: "Sends the applicant's risk assessment to SafetyNet Underwriting's own staff queue for a decision.",
        inputs: [
          { key: 'File', title: 'Risk assessment file', valueKind: 'String', format: 'field-ref', required: true },
          { key: 'ApplicantName', title: 'Applicant name', valueKind: 'String', format: 'field-ref', required: false },
          { key: 'Notes', title: 'Risk mitigation notes', valueKind: 'String', format: 'field-ref', required: false },
        ],
        outputs: [
          { key: 'insurerDecision', title: 'Insurer decision', valueKind: 'String', required: false },
          { key: 'insurerDecisionNotes', title: 'Insurer decision notes', valueKind: 'String', required: false },
        ],
        supportedCompletionModes: ['Poll', 'Webhook'],
        outcomes: [
          { key: 'approved', displayName: 'Approved' },
          { key: 'rejected', displayName: 'Rejected' },
        ],
      },
    ],
  },
];

export const SupportSystemCallActionConfiguration: Story = {
  args: {
    serviceBlueprint: SUPPORT_SYSTEM_CALL_SERVICE_BLUEPRINT,
    selectedStageKey: 'insurer-validation',
    componentCatalog: SUPPORT_SYSTEM_CALL_COMPONENT_CATALOG_FIXTURE,
    supportSystemCatalog: SUPPORT_SYSTEM_CATALOG_FIXTURE,
  },
  play: async ({ canvasElement }) => {
    await new Promise(resolve => setTimeout(resolve, 120));
    const el = canvasElement.querySelector('wayfinder-step-inspector') as WayfinderStepInspectorElement;
    await el.updateComplete;
    const root = el.shadowRoot!;

    const actionEditor = root.querySelector('wayfinder-stage-action-editor')!;
    const actionRoot = actionEditor.shadowRoot!;

    const supportSystemSelect = actionRoot.querySelector<HTMLSelectElement>('[data-wayfinder-support-system-select="0"]')!;
    await expect(supportSystemSelect.value).toBe('safetynet-underwriting');

    const capabilitySelect = actionRoot.querySelector<HTMLSelectElement>('[data-wayfinder-support-system-capability-select="0"]')!;
    await expect(capabilitySelect.value).toBe('validate-risk-assessment');

    // Both required and optional inputs render, already bound to the fixture's real field keys.
    await expect(actionRoot.querySelectorAll('.support-system-call-editor .property-object .field-block').length).toBeGreaterThanOrEqual(2);
    await expect(actionEditor.textContent).toContain('approved, rejected');
  },
};
