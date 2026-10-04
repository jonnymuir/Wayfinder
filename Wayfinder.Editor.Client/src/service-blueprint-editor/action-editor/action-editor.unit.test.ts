import { bulkDatasetMessages } from './bulk-dataset-action-editor.js';
import { formFields } from './forms-editor.js';
import { supportSystemCallMessages } from './support-system-call-editor.js';
import { BULK_DATASET_INGEST_TYPE, BULK_DATASET_MATERIALIZE_TYPE } from './bulk-dataset-schema.js';
import type { ActionFormFieldConfig, SupportSystemDescriptor } from '../types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

const field = (fieldKey: string): ActionFormFieldConfig => ({
  fieldKey,
  label: '',
  type: 'text',
  required: false,
  hintText: '',
  validationPattern: '',
  defaultValue: '',
  options: [],
});

const catalog = [
  {
    key: 'crm',
    displayName: 'CRM',
    description: '',
    capabilities: [
      {
        key: 'lookup',
        displayName: 'Lookup',
        description: '',
        inputs: [{ key: 'email', title: 'Email', required: true, valueKind: 'String' }],
        outcomes: [],
      },
    ],
  },
] as unknown as SupportSystemDescriptor[];

export function run(): number {
  failures = 0;

  const fields = [field('a'), field('b'), field('c')];
  check(
    'moving a field down swaps it with its neighbour',
    formFields
      .move(fields, 0, 1)
      .fields.map((f) => f.fieldKey)
      .join() === 'b,a,c'
  );
  check('moving the first field up leaves the list as it was', formFields.move(fields, 0, -1).to === 0);
  check('appending names the new field after the list length', formFields.append(fields)[3].fieldKey === 'field-4');
  check(
    'removing a field drops only that one',
    formFields
      .remove(fields, 1)
      .map((f) => f.fieldKey)
      .join() === 'a,c'
  );
  check(
    'patching a field changes only that field',
    formFields
      .patch(fields, 2, { label: 'Third' })
      .map((f) => f.label)
      .join() === ',,Third'
  );

  check(
    'with no support systems registered the author is told there is nothing to call',
    supportSystemCallMessages({ supportSystemKey: '', capabilityKey: '', inputs: {} }, []).length === 1
  );
  check(
    'the author is first asked to choose a support system',
    supportSystemCallMessages({ supportSystemKey: '', capabilityKey: '', inputs: {} }, catalog)[0] === 'Choose a support system.'
  );
  check(
    'an unknown support system is named',
    supportSystemCallMessages({ supportSystemKey: 'x', capabilityKey: '', inputs: {} }, catalog)[0].includes('“x”')
  );
  check(
    'then asked to choose a capability',
    supportSystemCallMessages({ supportSystemKey: 'crm', capabilityKey: '', inputs: {} }, catalog)[0] === 'Choose a capability.'
  );
  check(
    'a required input with no field bound is reported',
    supportSystemCallMessages({ supportSystemKey: 'crm', capabilityKey: 'lookup', inputs: {} }, catalog)[0] === '“Email” needs a field.'
  );
  check(
    'a fully configured call has nothing to fix',
    supportSystemCallMessages({ supportSystemKey: 'crm', capabilityKey: 'lookup', inputs: { email: 'contact' } }, catalog).length === 0
  );

  check('an empty ingest action lists what is missing', bulkDatasetMessages(BULK_DATASET_INGEST_TYPE, {}).length === 3);
  check(
    'an ingest with two row keys is rejected',
    bulkDatasetMessages(BULK_DATASET_INGEST_TYPE, {
      sourceFileField: 'f',
      datasetIdField: 'd',
      columns: [{ role: 'RowKey' }, { role: 'RowKey' }],
    })[0].includes('2 do')
  );
  check(
    'a complete ingest has nothing to fix',
    bulkDatasetMessages(BULK_DATASET_INGEST_TYPE, { sourceFileField: 'f', datasetIdField: 'd', columns: [{ role: 'RowKey' }] }).length === 0
  );
  check('a materialize action needs a dataset id and a target file', bulkDatasetMessages(BULK_DATASET_MATERIALIZE_TYPE, {}).length === 2);

  return failures;
}
