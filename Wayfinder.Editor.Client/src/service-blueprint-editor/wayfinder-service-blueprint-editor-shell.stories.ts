import type { Meta, StoryObj } from '@storybook/web-components-vite';
import './wayfinder-service-blueprint-editor-shell.js';
import type { WayfinderServiceBlueprintEditorShellElement } from './wayfinder-service-blueprint-editor-shell.js';
import { PAYMENT_DEMO_SERVICE_BLUEPRINT, PLANNING_SERVICE_BLUEPRINT, cloneAuthoredServiceBlueprint } from './fixtures/index.js';
import type { StageDefinition, ServiceBlueprint } from './types.js';
import { InMemoryServiceBlueprintSource } from './in-memory-service-blueprint-source.js';
import type { QueueDefinition } from './stage-assignment.js';

type ServiceBlueprintSeed = {
  blueprintKey: string;
  definitionKey: string;
  displayName: string;
  stages: Array<{
    stageKey: string;
    displayName: string;
    actor?: StageDefinition['actor'];
    stageType?: StageDefinition['stageType'];
    roleGates?: string[];
  }>;
  transitionActions: string[];
};

function cloneServiceBlueprint<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function buildServiceBlueprint(seed: ServiceBlueprintSeed): ServiceBlueprint {
  const serviceBlueprint = cloneServiceBlueprint(PLANNING_SERVICE_BLUEPRINT);
  const stages = seed.stages.map((stageSeed, index) => {
    const baseStage = serviceBlueprint.stages[Math.min(index, serviceBlueprint.stages.length - 1)];
    return {
      ...baseStage,
      stageKey: stageSeed.stageKey,
      displayName: stageSeed.displayName,
      actor: stageSeed.actor ?? baseStage.actor,
      stageType: stageSeed.stageType ?? baseStage.stageType,
      roleGates: stageSeed.roleGates ?? [],
    };
  });

  const gatewayKeyFor = (stageKey: string) => `route-from-${stageKey}`;
  const builtStages = stages.map((stage, index) => ({
    ...stage,
    routes:
      index < stages.length - 1
        ? [{ id: `${stage.stageKey}--route--${gatewayKeyFor(stage.stageKey)}`, target: gatewayKeyFor(stage.stageKey), trigger: 'route' }]
        : [],
  }));
  return {
    ...serviceBlueprint,
    definitionKey: seed.definitionKey,
    displayName: seed.displayName,
    initialStage: builtStages[0]?.stageKey ?? serviceBlueprint.initialStage,
    stages: builtStages,
    gateways: builtStages.slice(0, -1).map((stage, index) => {
      const targetKey = builtStages[index + 1].stageKey;
      const trigger = seed.transitionActions[index] ?? 'continue';
      return {
        key: gatewayKeyFor(stage.stageKey),
        displayName: `Route from ${stage.displayName}`,
        gatewayType: 'Split' as const,
        queueKey: stage.queueKey ?? 'public',
        actor: stage.actor,
        roleGates: [],
        routes: [{ id: `${gatewayKeyFor(stage.stageKey)}--${trigger}--${targetKey}`, target: targetKey, trigger }],
      };
    }),
  } as unknown as ServiceBlueprint;
}

function buildShellSource(): InMemoryServiceBlueprintSource {
  const planning = cloneServiceBlueprint(PLANNING_SERVICE_BLUEPRINT);
  const communityEnquiry = buildServiceBlueprint({
    blueprintKey: 'community-enquiry',
    definitionKey: 'community-enquiry',
    displayName: 'Community Enquiry',
    stages: [
      { stageKey: 'raise-enquiry', displayName: 'Raise enquiry', actor: 'public' },
      { stageKey: 'share-supporting-detail', displayName: 'Share supporting detail', actor: 'public' },
      {
        stageKey: 'review-enquiry',
        displayName: 'Review enquiry',
        actor: 'reviewer',
        stageType: 'TaskList',
        roleGates: ['reviewer'],
      },
      {
        stageKey: 'enquiry-closed',
        displayName: 'Enquiry closed',
        actor: 'reviewer',
        stageType: 'Confirmation',
        roleGates: ['reviewer'],
      },
    ],
    transitionActions: ['continue', 'send to review', 'close enquiry'],
  });
  const informationRequest = buildServiceBlueprint({
    blueprintKey: 'information-request',
    definitionKey: 'information-request',
    displayName: 'Information Request',
    stages: [
      { stageKey: 'request-summary', displayName: 'Request summary', actor: 'public' },
      { stageKey: 'upload-evidence', displayName: 'Upload evidence', actor: 'public' },
      {
        stageKey: 'review-response-pack',
        displayName: 'Review response pack',
        actor: 'reviewer',
        stageType: 'TaskList',
        roleGates: ['reviewer'],
      },
      {
        stageKey: 'response-sent',
        displayName: 'Response sent',
        actor: 'system',
        stageType: 'Confirmation',
        roleGates: ['reviewer'],
      },
    ],
    transitionActions: ['continue', 'submit evidence', 'send response'],
  });
  const paymentDemo = cloneAuthoredServiceBlueprint(PAYMENT_DEMO_SERVICE_BLUEPRINT);

  // blueprintKey for planning is 'planning' even though the definitionKey is
  // 'planning-application', so the shell's selector entries match the four
  // reference serviceBlueprints the existing Playwright suite drives.
  return new InMemoryServiceBlueprintSource([
    { blueprintKey: 'planning', serviceBlueprint: planning },
    { blueprintKey: 'community-enquiry', serviceBlueprint: communityEnquiry },
    { blueprintKey: 'information-request', serviceBlueprint: informationRequest },
    { blueprintKey: 'payment-demo', serviceBlueprint: paymentDemo },
  ]);
}

const REFERENCE_QUEUES: QueueDefinition[] = [
  { queueName: 'web-user', displayName: 'Applicant' },
  { queueName: 'business-user', displayName: 'Payments team' },
  { queueName: 'applicant', displayName: 'Applicant' },
  { queueName: 'public', displayName: 'Public' },
  { queueName: 'reviewer', displayName: 'Reviewer' },
  { queueName: 'payments', displayName: 'Payments' },
  { queueName: 'system', displayName: 'System' },
];

function makeShell(): WayfinderServiceBlueprintEditorShellElement {
  const element = document.createElement('wayfinder-service-blueprint-editor-shell') as WayfinderServiceBlueprintEditorShellElement;
  element.blueprintKey = 'planning';
  element.serviceBlueprintSource = buildShellSource();
  element.availableQueues = REFERENCE_QUEUES;
  element.style.cssText = 'display:block;min-height:860px;';
  return element;
}

const meta: Meta = {
  title: 'Service Blueprint Editor/Editor Shell',
  component: 'wayfinder-service-blueprint-editor-shell',
  tags: ['autodocs'],
  parameters: {
    layout: 'fullscreen',
    a11y: {
      config: {
        rules: [
          { id: 'color-contrast', enabled: true },
          { id: 'aria-required-children', enabled: true },
        ],
      },
    },
  },
  render: () => makeShell(),
};

export default meta;
type Story = StoryObj;

export const ReferenceShell: Story = {};

export const NarrowViewportTablet: Story = {
  parameters: {
    viewport: {
      defaultViewport: 'tablet',
    },
  },
  render: () => {
    const element = makeShell();
    element.style.cssText = 'display:block;width:768px;min-height:860px;';
    return element;
  },
};

export const NarrowViewportMobile: Story = {
  parameters: {
    viewport: {
      defaultViewport: 'mobile1',
    },
  },
  render: () => {
    const element = makeShell();
    element.style.cssText = 'display:block;width:375px;min-height:667px;';
    return element;
  },
};
