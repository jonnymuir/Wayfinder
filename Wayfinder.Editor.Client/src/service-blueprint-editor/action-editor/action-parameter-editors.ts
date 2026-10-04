import { html, nothing } from 'lit';
import type { ComponentPropertyDescriptor } from '../types.js';
import { cloneJsonValue, isFormsBackedAction, normaliseActionFormFields, validateAction } from '../action-editing.js';
import { renderActionMessages } from './action-messages.js';
import type { ActionEditorHost, ParamEditing } from './action-editor-host.js';
import {
  BULK_DATASET_INGEST_SCHEMA,
  BULK_DATASET_INGEST_TYPE,
  BULK_DATASET_MATERIALIZE_SCHEMA,
  BULK_DATASET_MATERIALIZE_TYPE,
  SUPPORT_SYSTEM_CALL_TYPE,
} from './bulk-dataset-schema.js';
import { renderBulkDatasetActionEditor } from './bulk-dataset-action-editor.js';
import { renderFormsEditor } from './forms-editor.js';
import { renderScalarParameter } from './scalar-parameter-field.js';
import { renderSupportSystemCallEditor } from './support-system-call-editor.js';

const BULK_DATASET_SCHEMAS: Record<string, ComponentPropertyDescriptor[]> = {
  [BULK_DATASET_INGEST_TYPE]: BULK_DATASET_INGEST_SCHEMA,
  [BULK_DATASET_MATERIALIZE_TYPE]: BULK_DATASET_MATERIALIZE_SCHEMA,
};

/** The parameters of an action whose shape comes from its catalog entry's `paramsSchema`: scalar fields, plus a form-field list for forms-backed actions. */
function renderCatalogDrivenEditor(host: ActionEditorHost, editing: ParamEditing, index: number) {
  const action = host.actions[index];
  const entry = host.actionEntry(action);
  const validation = validateAction(entry, action);
  const properties = entry?.paramsSchema.properties ?? [];
  const scalarProperties = properties.filter((property) => property.key !== 'fields');
  const hasFormFields = properties.some((property) => property.key === 'fields') && isFormsBackedAction(entry);

  return html`
    <div class="action-parameters">
      ${
        scalarProperties.length === 0
          ? nothing
          : html`
            <div class="field-grid">
              ${scalarProperties.map((definition) =>
                renderScalarParameter({
                  index,
                  definition,
                  value: action.params?.[definition.key],
                  error: validation.propertyErrors[definition.key],
                  onChange: (value) => editing.updateParam(index, definition.key, value),
                })
              )}
            </div>
          `
      }
      ${
        hasFormFields
          ? renderFormsEditor({
              index,
              get fields() {
                return normaliseActionFormFields(host.actions[index]?.params?.fields);
              },
              errors: validation.formFieldErrors,
              setFields: (fields) =>
                editing.updateParams(index, { ...(host.actions[index]?.params ?? {}), fields: cloneJsonValue(fields) }),
              announce: (message) => editing.announce(message),
            })
          : nothing
      }
      ${renderActionMessages(index, validation.messages)}
    </div>
  `;
}

/** The parameter editor for the action at `index`: a dedicated one for the action types that need it, otherwise driven by the catalog. */
export function renderActionParameters(host: ActionEditorHost, editing: ParamEditing, index: number) {
  const action = host.actions[index];
  if (!action) {
    return nothing;
  }

  if (action.type === SUPPORT_SYSTEM_CALL_TYPE) {
    return renderSupportSystemCallEditor({
      editing,
      catalog: host.supportSystemCatalog,
      fieldReferences: host.supportSystemFieldReferences,
      index,
      action,
    });
  }

  const bulkSchema = BULK_DATASET_SCHEMAS[action.type];
  if (bulkSchema) {
    return renderBulkDatasetActionEditor({
      editing,
      fieldReferences: host.supportSystemFieldReferences,
      index,
      actionType: action.type,
      schema: bulkSchema,
      params: action.params ?? {},
    });
  }

  return renderCatalogDrivenEditor(host, editing, index);
}
