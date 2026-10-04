import { hydrateServiceBlueprintDefinition } from '../blueprint-hydration.js';
import type { ServiceBlueprint } from '../types.js';

export const STUB_SERVICE_BLUEPRINT: ServiceBlueprint = hydrateServiceBlueprintDefinition({
  definitionKey: 'planning-permission',
  displayName: 'Planning Permission Application',
  version: 1,
  initialStage: 'applicant-details',
  requestPolicy: 'single',
  schemaVersion: '1.0',
  queues: [],
  stages: [
    {
      actions: [
        {
          type: 'forms.load',
          parameterSchemaKey: 'forms.form-reference',
          params: {
            formDefinitionId: 'planning-applicant-details',
          },
          summary: 'Load the applicant details form.',
          timing: 'onEnter',
        },
        {
          type: 'notifications.send-email',
          parameterSchemaKey: 'notifications.send-email',
          params: {
            recipientEmail: 'planning.officers@council.example',
            subject: 'Planning application started',
            templateId: 'planning-started',
          },
          summary: 'Send email to Planning Officers',
          timing: 'onEnter',
        },
      ],
      actor: 'public',
      components: [],
      description: 'Collect applicant details and site context.',
      displayName: 'Applicant Details',
      roleGates: [],
      routes: [
        {
          actions: [],
          id: 'applicant-details--route--route-check-answers',
          target: 'route-check-answers',
          trigger: 'route',
        },
        {
          id: 'applicant-details--submit--route-check-answers',
          target: 'route-check-answers',
          trigger: 'submit',
        },
      ],
      stageKey: 'applicant-details',
      stageType: 'Question',
    },
    {
      actions: [],
      actor: 'public',
      components: [],
      description: 'Review the captured answers before submission.',
      displayName: 'Check Your Answers',
      roleGates: [],
      routes: [
        {
          actions: [],
          id: 'check-answers--route--route-reviewer-assessment',
          target: 'route-reviewer-assessment',
          trigger: 'route',
        },
        {
          id: 'check-answers--submit--route-reviewer-assessment',
          target: 'route-reviewer-assessment',
          trigger: 'submit',
        },
      ],
      stageKey: 'check-answers',
      stageType: 'CheckAnswers',
    },
    {
      actions: [
        {
          type: 'case.assign',
          parameterSchemaKey: 'case.assign',
          params: {
            assigneeType: 'role',
            assigneeValue: 'reviewer',
            overwriteExisting: false,
          },
          summary: 'Assign the case to a reviewer.',
          timing: 'onEnter',
        },
        {
          type: 'forms.request-evidence',
          parameterSchemaKey: 'forms.request-evidence',
          params: {
            dueDate: '',
            fields: [
              {
                type: 'textarea',
                defaultValue: '',
                fieldKey: 'decision-note',
                hintText: 'Explain why the reviewer is requesting more evidence.',
                label: 'Decision note',
                options: [],
                required: true,
                validationPattern: '',
              },
            ],
            helpText: 'Capture any extra evidence the reviewer needs before deciding.',
            title: 'Request supporting evidence',
          },
          summary: 'Request evidence form: 1 field',
          timing: 'onEnter',
        },
      ],
      actor: 'reviewer',
      components: [],
      description: 'Internal assessment and decision making.',
      displayName: 'Reviewer Assessment',
      roleGates: ['reviewer'],
      routes: [
        {
          actions: [],
          id: 'reviewer-assessment--route--route-reviewer-decision',
          target: 'route-reviewer-decision',
          trigger: 'route',
        },
        {
          id: 'reviewer-assessment--approve--route-reviewer-decision',
          target: 'route-reviewer-decision',
          trigger: 'approve',
        },
      ],
      stageKey: 'reviewer-assessment',
      stageType: 'Question',
    },
    {
      actions: [],
      actor: 'public',
      components: [],
      description: 'Confirm the application has been submitted.',
      displayName: 'Application Submitted',
      roleGates: [],
      routes: [],
      stageKey: 'confirmation',
      stageType: 'Confirmation',
    },
  ],
  gateways: [
    {
      displayName: 'Route to check answers',
      gatewayType: 'Split',
      key: 'route-check-answers',
      queueKey: 'public',
      requiredIncomingQueues: [],
      roleGates: [],
      routes: [
        {
          actions: [
            {
              type: 'forms.submit',
              parameterSchemaKey: 'forms.form-reference',
              params: {
                formDefinitionId: 'planning-applicant-details',
              },
              summary: 'Submit the applicant details form.',
              timing: 'onTransition',
            },
          ],
          id: 'applicant-details--submit--check-answers',
          target: 'check-answers',
          trigger: 'submit',
        },
        {
          actions: [
            {
              type: 'forms.submit',
              parameterSchemaKey: 'forms.form-reference',
              params: {
                formDefinitionId: 'planning-applicant-details',
              },
              summary: 'Submit the applicant details form.',
              timing: 'onTransition',
            },
          ],
          id: 'route-check-answers--submit--check-answers',
          target: 'check-answers',
          trigger: 'submit',
        },
      ],
    },
    {
      displayName: 'Route to reviewer assessment',
      gatewayType: 'Split',
      key: 'route-reviewer-assessment',
      queueKey: 'public',
      requiredIncomingQueues: [],
      roleGates: [],
      routes: [
        {
          actions: [],
          id: 'check-answers--submit--reviewer-assessment',
          target: 'reviewer-assessment',
          trigger: 'submit',
        },
        {
          actions: [],
          id: 'route-reviewer-assessment--submit--reviewer-assessment',
          target: 'reviewer-assessment',
          trigger: 'submit',
        },
      ],
    },
    {
      displayName: 'Route from reviewer assessment',
      gatewayType: 'Split',
      key: 'route-reviewer-decision',
      queueKey: 'reviewer',
      requiredIncomingQueues: [],
      roleGates: [],
      routes: [
        {
          actions: [],
          id: 'reviewer-assessment--approve--confirmation',
          requiresRole: 'reviewer',
          target: 'confirmation',
          trigger: 'approve',
        },
        {
          actions: [],
          id: 'reviewer-assessment--reject--applicant-details',
          requiresRole: 'reviewer',
          target: 'applicant-details',
          trigger: 'reject',
        },
        {
          actions: [],
          id: 'route-reviewer-decision--approve--confirmation',
          requiresRole: 'reviewer',
          target: 'confirmation',
          trigger: 'approve',
        },
        {
          actions: [],
          id: 'route-reviewer-decision--reject--applicant-details',
          requiresRole: 'reviewer',
          target: 'applicant-details',
          trigger: 'reject',
        },
      ],
    },
  ],
});
