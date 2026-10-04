import { type ComponentPropertyDescriptor, componentPropertyValueKindValues } from '../types.js';

export const SUPPORT_SYSTEM_CALL_TYPE = 'support-system-call';

// bulk-dataset-ingest/bulk-dataset-materialize (see docs/guides/bulk-data-review.md) — genuinely
// real, ProcessManagerEngine-executed action types, the same "not a fictional mockup" status as
// SUPPORT_SYSTEM_CALL_TYPE above. Unlike that one, their params shape is static (not dependent on
// a live-fetched catalog), so the whole editor — scalar field-refs and the repeatable `columns`
// list alike — is just one renderComponentPropertyFields call against a hand-authored schema,
// reusing its existing Array-of-Object recursion (the same mechanism a stat-group's `items` or a
// chart's `bands` already gets for free) rather than a bespoke list-editing implementation.
export const BULK_DATASET_INGEST_TYPE = 'bulk-dataset-ingest';
export const BULK_DATASET_MATERIALIZE_TYPE = 'bulk-dataset-materialize';

// Mirrors Wayfinder.Models.ServiceDesign.BulkData.BulkDatasetColumnRole — keep in sync if that C#
// enum changes (action params are untyped JSON, so it is not part of the generated model). A
// column's value kinds come from the generated model's ComponentPropertyValueKind.
const BULK_DATASET_COLUMN_ROLES = ['RowKey', 'Data', 'ResponseMatchedId', 'ResponseError', 'ResponseWarning', 'Ignored'];

const BULK_DATASET_COLUMN_SCHEMA: ComponentPropertyDescriptor = {
  key: 'column',
  title: 'Column',
  valueKind: 'Object',
  required: false,
  properties: [
    { key: 'key', title: 'Column key', description: 'The literal CSV header this column binds to.', valueKind: 'String', required: true },
    { key: 'title', title: 'Title', description: 'Label shown in the review UI.', valueKind: 'String', required: true },
    { key: 'valueKind', title: 'Value kind', valueKind: 'String', required: true, allowedValues: [...componentPropertyValueKindValues] },
    {
      key: 'format',
      title: 'Format',
      description: 'Optional semantic hint, e.g. "currency", "date".',
      valueKind: 'String',
      required: false,
    },
    { key: 'role', title: 'Role', valueKind: 'String', required: true, allowedValues: BULK_DATASET_COLUMN_ROLES },
    { key: 'visible', title: 'Visible', valueKind: 'Boolean', required: false, editor: 'toggle', defaultValue: true },
    { key: 'editable', title: 'Editable (Data role only)', valueKind: 'Boolean', required: false, editor: 'toggle' },
  ],
};

export const BULK_DATASET_INGEST_SCHEMA: ComponentPropertyDescriptor[] = [
  {
    key: 'sourceFileField',
    title: 'Source file field',
    valueKind: 'String',
    format: 'field-ref',
    required: true,
    description: 'The file to parse — typically a support-system-call action’s own declared file output.',
  },
  {
    key: 'datasetIdField',
    title: 'Dataset id field',
    valueKind: 'String',
    required: true,
    description:
      'A new field name the minted dataset id is written into — not a field-ref: this name doesn’t exist yet, ingest creates it. A bulk-dataset-materialize action or a bulk-data-review component binds to it.',
  },
  {
    key: 'errorCountField',
    title: 'Error count field',
    valueKind: 'String',
    required: false,
    description: 'Optional new field name the error row count is written into.',
  },
  {
    key: 'warningCountField',
    title: 'Warning count field',
    valueKind: 'String',
    required: false,
    description: 'Optional new field name the warning row count is written into.',
  },
  {
    key: 'acceptedCountField',
    title: 'Accepted count field',
    valueKind: 'String',
    required: false,
    description: 'Optional new field name the accepted row count is written into.',
  },
  {
    key: 'dirtyCountField',
    title: 'Dirty count field',
    valueKind: 'String',
    required: false,
    description:
      'Optional new field name the number of rows currently edited-since-last-check is written into — 0 right after ingest, kept live as corrections/reverts happen while sitting on this stage. Reference it in a showWhen condition (declared under calculations.fields with source: "service") to block finishing until the file is resubmitted or those edits are discarded — see docs/guides/bulk-data-review.md’s sync-state section.',
  },
  {
    key: 'columns',
    title: 'Columns',
    valueKind: 'Array',
    required: true,
    items: BULK_DATASET_COLUMN_SCHEMA,
    description: 'One entry per CSV column — the only place this dataset’s shape is authored. Exactly one column must have role RowKey.',
  },
];

export const BULK_DATASET_MATERIALIZE_SCHEMA: ComponentPropertyDescriptor[] = [
  {
    key: 'datasetIdField',
    title: 'Dataset id field',
    valueKind: 'String',
    required: true,
    description:
      'Must match a bulk-dataset-ingest action’s own datasetIdField — not a field-ref picker, since it names a field an ingest action elsewhere declares, not one already captured.',
  },
  {
    key: 'targetFileField',
    title: 'Target file field',
    valueKind: 'String',
    format: 'field-ref',
    required: true,
    description: 'The materialized file is written here — typically the same field the original upload went to.',
  },
];
