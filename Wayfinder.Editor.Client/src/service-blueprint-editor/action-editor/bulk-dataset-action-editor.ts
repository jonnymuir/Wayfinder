import { html } from 'lit';
import type { ComponentPropertyDescriptor } from '../types.js';
import { renderComponentPropertyFields, setAtPath, type ResolvedPropertyReferences } from '../component-property-editor.js';
import type { FieldReference } from '../component-property-references.js';
import { renderActionMessages } from './action-messages.js';
import type { ParamEditing } from './action-editor-host.js';
import { BULK_DATASET_INGEST_TYPE } from './bulk-dataset-schema.js';

type Params = Record<string, unknown>;

function ingestMessages(params: Params): string[] {
  const messages: string[] = [];
  if (!params.sourceFileField) {
    messages.push('Set a source file field.');
  }
  if (!params.datasetIdField) {
    messages.push('Set a dataset id field.');
  }

  const columns = Array.isArray(params.columns) ? (params.columns as Params[]) : [];
  if (columns.length === 0) {
    messages.push('Add at least one column.');
    return messages;
  }

  const rowKeyCount = columns.filter((column) => column.role === 'RowKey').length;
  if (rowKeyCount === 0) {
    messages.push('Exactly one column must have role RowKey — none do yet.');
  } else if (rowKeyCount > 1) {
    messages.push(`Exactly one column must have role RowKey — ${rowKeyCount} do.`);
  }
  return messages;
}

function materializeMessages(params: Params): string[] {
  const messages: string[] = [];
  if (!params.datasetIdField) {
    messages.push('Set a dataset id field, matching a bulk-dataset-ingest action’s own.');
  }
  if (!params.targetFileField) {
    messages.push('Set a target file field.');
  }
  return messages;
}

/**
 * Early, inline nudges for a bulk-dataset action's parameters. Hand-computed rather than derived from
 * `paramsSchema` (left empty on both actions' catalog entries — their shape is static, not catalog
 * driven); the authoritative structural check is server-side (`ValidateBulkDatasetActions`), surfaced
 * through the editor's live-diagnostics panel.
 */
export function bulkDatasetMessages(actionType: string, params: Params): string[] {
  return actionType === BULK_DATASET_INGEST_TYPE ? ingestMessages(params) : materializeMessages(params);
}

/**
 * The dedicated editor for bulk-dataset-ingest/bulk-dataset-materialize actions (see
 * docs/guides/bulk-data-review.md). Unlike support-system-call's, the whole thing — scalar
 * field-refs and the repeatable `columns` list alike — is one `renderComponentPropertyFields` call
 * against a static schema, since neither action's shape depends on a value chosen while authoring it.
 */
export function renderBulkDatasetActionEditor({
  editing,
  fieldReferences,
  index,
  actionType,
  schema,
  params,
}: {
  editing: ParamEditing;
  fieldReferences: FieldReference[];
  index: number;
  actionType: string;
  schema: ComponentPropertyDescriptor[];
  params: Params;
}) {
  const references: ResolvedPropertyReferences = {
    siblingFields: fieldReferences,
    allFields: fieldReferences,
    stageOptions: [],
    calculationFieldNames: [],
  };

  return html`
    <div class="action-parameters bulk-dataset-action-editor" data-wayfinder-bulk-dataset-action-editor="${index}">
      ${renderComponentPropertyFields(schema, {
        value: params,
        onChange: (path, value) => editing.updateParams(index, setAtPath(params, path, value) as Params),
        idPrefix: `bulk-dataset-action-${index}`,
        references,
      })}
      ${renderActionMessages(index, bulkDatasetMessages(actionType, params))}
    </div>
  `;
}
