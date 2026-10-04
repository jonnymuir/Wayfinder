import type { ActionCatalogEntry } from './types.js';

export const BUILT_IN_ACTION_CATALOG: ActionCatalogEntry[] = [
  // Unlike every other entry below (fictional, host-defined mockups — see this section's own
  // header comment), this one is real: ProcessManagerEngine.cs actually executes a
  // support-system-call action found on a stage's onEnter timing (see
  // docs/guides/support-systems.md). Its params shape is genuinely dynamic — which fields it
  // needs depends on which support system + capability the author picks — so paramsSchema stays
  // empty here and wayfinder-stage-action-editor.ts renders a dedicated editor for this one
  // action type instead of the generic paramsSchema-driven form, driven by the live support
  // system catalog (support-system-catalog.ts) rather than this static entry.
  {
    type: 'support-system-call',
    label: 'Call a support system',
    summary: 'Call a registered support system capability when this stage opens, and wait for its outcome.',
    appliesTo: ['stage.onEntry'],
    paramsSchema: {
      key: 'support-system-call.params',
      title: 'Support system call',
      valueKind: 'Object',
      properties: [],
    },
    defaultParams: { supportSystemKey: '', capabilityKey: '', inputs: {} },
    status: 'available',
    runtimeImplementation: 'wayfinder-engine',
  },
  // Also real (see docs/guides/bulk-data-review.md) — ProcessManagerEngine genuinely executes
  // both onEnter. Unlike support-system-call, their params shape is static, not dependent on a
  // live-fetched catalog, but wayfinder-stage-action-editor.ts still renders a dedicated editor
  // for the same reason: paramsSchema stays empty here, and hand-computed validation messages
  // replace the generic paramsSchema-driven required-field check (see
  // WayfinderServiceBlueprintActionEditorElement._renderBulkDatasetActionEditor).
  {
    type: 'bulk-dataset-ingest',
    label: 'Ingest a bulk dataset',
    summary: 'Parse a file field into an indexed, pageable dataset a bulk-data-review component can show.',
    appliesTo: ['stage.onEntry'],
    paramsSchema: {
      key: 'bulk-dataset-ingest.params',
      title: 'Bulk dataset ingest',
      valueKind: 'Object',
      properties: [],
    },
    defaultParams: { sourceFileField: '', datasetIdField: '', columns: [] },
    status: 'available',
    runtimeImplementation: 'wayfinder-engine',
  },
  {
    type: 'bulk-dataset-materialize',
    label: 'Materialize a bulk dataset',
    summary: 'Reconstruct a previously-ingested dataset (with any corrections) back into a file field.',
    appliesTo: ['stage.onEntry'],
    paramsSchema: {
      key: 'bulk-dataset-materialize.params',
      title: 'Bulk dataset materialize',
      valueKind: 'Object',
      properties: [],
    },
    defaultParams: { datasetIdField: '', targetFileField: '' },
    status: 'available',
    runtimeImplementation: 'wayfinder-engine',
  },
  {
    type: 'forms.load',
    label: 'Load form',
    summary: 'Load a forms-engine definition when a stage opens.',
    appliesTo: ['stage.onEntry'],
    paramsSchema: {
      key: 'forms.form-reference',
      title: 'Forms engine reference',
      valueKind: 'Object',
      properties: [
        {
          key: 'formDefinitionId',
          title: 'Form definition id',
          valueKind: 'String',
          editor: 'text',
        },
      ],
      required: ['formDefinitionId'],
    },
    defaultParams: { formDefinitionId: '' },
    status: 'available',
    runtimeImplementation: 'reference-business-app',
  },
  {
    type: 'forms.save',
    label: 'Save form',
    summary: 'Persist the current forms-engine payload before leaving a stage.',
    appliesTo: ['stage.onExit'],
    paramsSchema: {
      key: 'forms.form-reference',
      title: 'Forms engine reference',
      valueKind: 'Object',
      properties: [
        {
          key: 'formDefinitionId',
          title: 'Form definition id',
          valueKind: 'String',
          editor: 'text',
        },
      ],
      required: ['formDefinitionId'],
    },
    defaultParams: { formDefinitionId: '' },
    status: 'available',
    runtimeImplementation: 'reference-business-app',
  },
  {
    type: 'forms.submit',
    label: 'Submit form',
    summary: 'Validate and submit a forms-engine definition while taking a transition.',
    appliesTo: ['transition'],
    paramsSchema: {
      key: 'forms.form-reference',
      title: 'Forms engine reference',
      valueKind: 'Object',
      properties: [
        {
          key: 'formDefinitionId',
          title: 'Form definition id',
          valueKind: 'String',
          editor: 'text',
        },
      ],
      required: ['formDefinitionId'],
    },
    defaultParams: { formDefinitionId: '' },
    status: 'available',
    runtimeImplementation: 'reference-business-app',
  },
  {
    type: 'case.assign',
    label: 'Assign case',
    summary: 'Assign the current case to a role, queue, or named user.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'case.assign',
      title: 'Case assignment',
      valueKind: 'Object',
      properties: [
        {
          key: 'assigneeType',
          title: 'Assignment target type',
          valueKind: 'String',
          editor: 'select',
          allowedValues: ['role', 'queue', 'user'],
          defaultValue: 'role',
        },
        { key: 'assigneeValue', title: 'Assignment target', valueKind: 'String', editor: 'text' },
        {
          key: 'overwriteExisting',
          title: 'Overwrite existing assignment',
          valueKind: 'Boolean',
          editor: 'toggle',
          defaultValue: false,
        },
      ],
      required: ['assigneeType', 'assigneeValue'],
    },
    defaultParams: { assigneeType: 'role', assigneeValue: '', overwriteExisting: false },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'case.enqueue',
    label: 'Enqueue case',
    summary: 'Place the case into a named queue with an optional priority.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'case.enqueue',
      title: 'Queue placement',
      valueKind: 'Object',
      properties: [
        { key: 'queue', title: 'Queue', valueKind: 'String', editor: 'text' },
        {
          key: 'priority',
          title: 'Priority',
          valueKind: 'String',
          editor: 'select',
          allowedValues: ['low', 'normal', 'high'],
          defaultValue: 'normal',
        },
      ],
      required: ['queue'],
    },
    defaultParams: { queue: '', priority: 'normal' },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'case.set-status',
    label: 'Set case status',
    summary: 'Update the case status shown to staff and applicants.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'case.set-status',
      title: 'Case status',
      valueKind: 'Object',
      properties: [
        { key: 'status', title: 'Status', valueKind: 'String', editor: 'text' },
        { key: 'reason', title: 'Reason', valueKind: 'String', editor: 'textarea' },
      ],
      required: ['status'],
    },
    defaultParams: { status: '', reason: '' },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'case.add-note',
    label: 'Add case note',
    summary: 'Attach an internal or public note to the current case.',
    appliesTo: ['stage.onExit', 'transition'],
    paramsSchema: {
      key: 'case.add-note',
      title: 'Case note',
      valueKind: 'Object',
      properties: [
        { key: 'note', title: 'Note', valueKind: 'String', editor: 'textarea' },
        {
          key: 'visibility',
          title: 'Visibility',
          valueKind: 'String',
          editor: 'select',
          allowedValues: ['internal', 'public'],
          defaultValue: 'internal',
        },
      ],
      required: ['note'],
    },
    defaultParams: { note: '', visibility: 'internal' },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'notifications.send-email',
    label: 'Send email',
    summary: 'Queue an email notification using a named template.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'notifications.send-email',
      title: 'Email notification',
      valueKind: 'Object',
      properties: [
        { key: 'templateId', title: 'Template id', valueKind: 'String', editor: 'text' },
        {
          key: 'recipientEmail',
          title: 'Recipient email',
          valueKind: 'String',
          format: 'email',
          editor: 'text',
        },
        { key: 'subject', title: 'Subject override', valueKind: 'String', editor: 'text' },
      ],
      required: ['templateId', 'recipientEmail'],
    },
    defaultParams: { templateId: '', recipientEmail: '', subject: '' },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'notifications.send-sms',
    label: 'Send SMS',
    summary: 'Queue an SMS notification using a named template.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'notifications.send-sms',
      title: 'SMS notification',
      valueKind: 'Object',
      properties: [
        { key: 'templateId', title: 'Template id', valueKind: 'String', editor: 'text' },
        { key: 'recipientNumber', title: 'Recipient number', valueKind: 'String', editor: 'text' },
      ],
      required: ['templateId', 'recipientNumber'],
    },
    defaultParams: { templateId: '', recipientNumber: '' },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
  {
    type: 'forms.request-evidence',
    label: 'Request evidence form',
    summary: 'Ask the applicant for supporting evidence using a configured response form.',
    appliesTo: ['stage.onEntry', 'transition'],
    paramsSchema: {
      key: 'forms.request-evidence',
      title: 'Evidence request',
      valueKind: 'Object',
      properties: [
        { key: 'title', title: 'Prompt title', valueKind: 'String', editor: 'text' },
        { key: 'helpText', title: 'Intro help text', valueKind: 'String', editor: 'textarea' },
        { key: 'dueDate', title: 'Due date', valueKind: 'String', format: 'date', editor: 'date' },
        {
          key: 'fields',
          title: 'Fields',
          valueKind: 'Array',
          editor: 'collection',
          items: {
            key: 'field',
            title: 'Field',
            valueKind: 'Object',
            properties: [
              { key: 'fieldKey', title: 'Field key', valueKind: 'String', editor: 'text' },
              { key: 'label', title: 'Label', valueKind: 'String', editor: 'text' },
              {
                key: 'type',
                title: 'Field type',
                valueKind: 'String',
                editor: 'select',
                allowedValues: ['text', 'number', 'textarea', 'select', 'radio', 'date'],
                defaultValue: 'text',
              },
              { key: 'required', title: 'Required', valueKind: 'Boolean', editor: 'toggle', defaultValue: false },
              { key: 'hintText', title: 'Help text', valueKind: 'String', editor: 'textarea' },
              { key: 'validationPattern', title: 'Validation pattern', valueKind: 'String', editor: 'text' },
              { key: 'defaultValue', title: 'Default value', valueKind: 'String', editor: 'text' },
              {
                key: 'options',
                title: 'Options',
                valueKind: 'Array',
                editor: 'collection',
                items: {
                  key: 'option',
                  title: 'Option',
                  valueKind: 'String',
                  editor: 'text',
                },
              },
            ],
          },
        },
      ],
      required: ['title', 'fields'],
    },
    defaultParams: {
      title: 'Request supporting evidence',
      helpText: 'Explain what evidence the applicant should upload or complete.',
      dueDate: '',
      fields: [
        {
          fieldKey: 'supporting-evidence',
          label: 'Supporting evidence',
          type: 'select',
          required: true,
          hintText: 'Choose the evidence the applicant needs to provide.',
          validationPattern: '',
          defaultValue: '',
          options: ['Site photos', 'Ownership certificate', 'Tree survey'],
        },
      ],
    },
    status: 'planned',
    runtimeImplementation: 'planned',
  },
];
