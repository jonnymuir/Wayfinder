using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public partial record ServiceBlueprint
{
    /// <summary>
    /// Every field key a <c>bulk-dataset-ingest</c> action anywhere in this blueprint declares
    /// via its <c>datasetIdField</c>/<c>errorCountField</c>/<c>warningCountField</c>/
    /// <c>acceptedCountField</c>/<c>dirtyCountField</c> params —
    /// <see cref="ValidateDataDisplayBindings"/>'s "known field" set for stat-group/summary-list
    /// bindings, the same role <see cref="GetSupportSystemOutputFieldKeys"/> plays for a support
    /// system's own declared <c>Outputs</c>.
    /// </summary>
    private HashSet<string> GetBulkDatasetIngestOutputFieldKeys()
    {
        var outputFieldKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            foreach (var action in stage.Actions ?? [])
            {
                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var countFieldParam in new[] { "datasetIdField", "errorCountField", "warningCountField", "acceptedCountField", "dirtyCountField" })
                {
                    var fieldKey = action.Parameters[countFieldParam]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(fieldKey))
                    {
                        outputFieldKeys.Add(fieldKey);
                    }
                }
            }
        }

        return outputFieldKeys;
    }

    /// <summary>
    /// Validates every <c>bulk-dataset-ingest</c>/<c>bulk-dataset-materialize</c> action. An
    /// ingest action must set <c>sourceFileField</c>, resolving to a known field (a captured
    /// input, or a support-system capability's own declared output — the response file from an
    /// external system's <c>support-system-call</c> is the expected common case), and
    /// <c>datasetIdField</c> — the field the minted dataset id is written into, the single
    /// identifier a later <c>bulk-dataset-materialize</c> action or a <c>BulkDataReviewComponent</c>
    /// binds to (deliberately not <c>sourceFileField</c> itself: a materialize action runs on a
    /// different stage than ingest, sometimes several loop rounds later, and only ever has
    /// <c>ServiceRequest.FieldValues</c> to read from — <c>datasetIdField</c> is how it finds
    /// "the dataset ingest already produced" without the engine needing any dataset registry of
    /// its own). It must also declare at least one column, exactly one of which is
    /// <see cref="BulkData.BulkDatasetColumnRole.RowKey"/> (never zero, never more than one — the
    /// external system can only be expected to echo back a single correlation column); no two
    /// columns may share a <c>key</c>; and every column's <c>role</c>/<c>valueKind</c> must be one
    /// of the closed, known vocabularies. A <c>bulk-dataset-materialize</c> action must set
    /// <c>datasetIdField</c> (matching some ingest action's own) and <c>targetFileField</c>. See
    /// docs/guides/bulk-data-review.md.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateBulkDatasetActions()
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>();
        var knownFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        knownFieldKeys.UnionWith(GetSupportSystemOutputFieldKeys());

        var ingestDatasetIdFields = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: bulk-dataset-ingest actions only — collects every declared datasetIdField
        // first, so pass 2 (below) can validate a materialize action against the *complete* set
        // regardless of which stage/action ends up earlier in iteration order.
        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
                {
                    continue;
                }

                var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_DATASET_ID_FIELD",
                        $"{path}.params.datasetIdField",
                        "A bulk-dataset-ingest action must set params.datasetIdField."));
                    continue;
                }

                ingestDatasetIdFields.Add(datasetIdField);

                var sourceFileField = action.Parameters["sourceFileField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(sourceFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_SOURCE_FIELD",
                        $"{path}.params.sourceFileField",
                        "A bulk-dataset-ingest action must set params.sourceFileField."));
                }
                else if (!knownFieldKeys.Contains(sourceFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_INVALID_SOURCE_FIELD",
                        $"{path}.params.sourceFileField",
                        $"sourceFileField '{sourceFileField}' is neither a captured input field nor a " +
                        "support system capability's declared output anywhere in this blueprint."));
                }

                var columns = action.Parameters["columns"]?.AsArray();
                if (columns is null || columns.Count == 0)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_COLUMNS",
                        $"{path}.params.columns",
                        "A bulk-dataset-ingest action must declare at least one column."));
                    continue;
                }

                var seenColumnKeys = new HashSet<string>(StringComparer.Ordinal);
                var rowKeyColumnCount = 0;
                var columnIndex = 0;
                foreach (var columnNode in columns)
                {
                    var columnPath = $"{path}.params.columns[{columnIndex}]";
                    columnIndex++;

                    var column = columnNode?.AsObject();
                    var columnKey = column?["key"]?.GetValue<string>();
                    var columnTitle = column?["title"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(columnKey) || string.IsNullOrWhiteSpace(columnTitle))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_INVALID_COLUMN",
                            columnPath,
                            "Every column must set both key and title."));
                        continue;
                    }

                    if (!seenColumnKeys.Add(columnKey))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_DUPLICATE_COLUMN_KEY",
                            $"{columnPath}.key",
                            $"Column key '{columnKey}' is declared more than once."));
                    }

                    var roleValue = column?["role"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(roleValue) ||
                        !Enum.TryParse<BulkData.BulkDatasetColumnRole>(roleValue, out var role))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_UNKNOWN_ROLE",
                            $"{columnPath}.role",
                            $"Column '{columnKey}' has role '{roleValue}', which is not a recognised " +
                            $"BulkDatasetColumnRole ({string.Join(", ", Enum.GetNames<BulkData.BulkDatasetColumnRole>())})."));
                        continue;
                    }

                    if (role == BulkData.BulkDatasetColumnRole.RowKey)
                    {
                        rowKeyColumnCount++;
                    }

                    var valueKindValue = column?["valueKind"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(valueKindValue) ||
                        !Enum.TryParse<ComponentPropertyValueKind>(valueKindValue, out _))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_UNKNOWN_VALUE_KIND",
                            $"{columnPath}.valueKind",
                            $"Column '{columnKey}' has valueKind '{valueKindValue}', which is not a recognised " +
                            "ComponentPropertyValueKind."));
                    }
                }

                if (rowKeyColumnCount == 0)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_ROW_KEY",
                        $"{path}.params.columns",
                        "Exactly one column must declare role RowKey — the column the external system is " +
                        "expected to echo back unchanged, used to correlate a row across resubmission rounds. " +
                        "None of this action's columns declare it."));
                }
                else if (rowKeyColumnCount > 1)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_DUPLICATE_ROW_KEY_ROLE",
                        $"{path}.params.columns",
                        $"{rowKeyColumnCount} columns declare role RowKey — exactly one column must, since " +
                        "it's the single correlation key used to match a row across resubmission rounds."));
                }
            }
        }

        // Pass 2: bulk-dataset-materialize actions, validated against the complete set pass 1 collected.
        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetMaterialize, StringComparison.Ordinal))
                {
                    continue;
                }

                var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_DATASET_ID_FIELD",
                        $"{path}.params.datasetIdField",
                        "A bulk-dataset-materialize action must set params.datasetIdField."));
                }
                else if (!ingestDatasetIdFields.Contains(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_UNKNOWN_DATASET",
                        $"{path}.params.datasetIdField",
                        $"datasetIdField '{datasetIdField}' doesn't match any bulk-dataset-ingest action's own " +
                        "datasetIdField in this blueprint — there's no dataset for this action to materialize."));
                }

                var targetFileField = action.Parameters["targetFileField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(targetFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_TARGET_FIELD",
                        $"{path}.params.targetFileField",
                        "A bulk-dataset-materialize action must set params.targetFileField."));
                }
            }
        }

        return diagnostics;
    }
}
