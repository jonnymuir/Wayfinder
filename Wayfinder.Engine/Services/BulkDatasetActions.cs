using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Runs a stage's <c>bulk-dataset-ingest</c> and <c>bulk-dataset-materialize</c> actions when a cursor
/// lands on it. See docs/guides/bulk-data-review.md. A missing store, bad parameters or a failing
/// ingest skips the action (and logs why) rather than failing the request.
/// </summary>
internal sealed partial class BulkDatasetActions(IBulkDatasetStore? bulkDatasetStore, ILogger logger)
{
    private static readonly IReadOnlyDictionary<string, object?> NoUpdates =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Finds the <c>bulk-dataset-ingest</c> action (anywhere in the blueprint) whose own
    /// <c>datasetIdField</c>, read back off this instance's current <paramref name="fieldValues"/>,
    /// equals <paramref name="datasetId"/> — the same cross-reference
    /// <c>bulk-dataset-materialize</c>'s own <c>datasetIdField</c> match already relies on.
    /// </summary>
    public static ActionDefinition? FindDeclaringIngestAction(
        ServiceBlueprint definition, IReadOnlyDictionary<string, object?> fieldValues, string datasetId) =>
        definition.Stages
            .SelectMany(stage => stage.Actions ?? [])
            .FirstOrDefault(action =>
                string.Equals(action.Type, BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal)
                && action.Parameters["datasetIdField"]?.GetValue<string>() is { Length: > 0 } datasetIdField
                && string.Equals(ReadStringFieldValue(fieldValues, datasetIdField), datasetId, StringComparison.Ordinal));

    /// <summary>
    /// Reads a field value that's always been written as a plain string (a datasetId, never
    /// anything else) — tolerant of the same fresh-CLR-value-vs-reloaded-JsonElement split
    /// <see cref="GetDisplayValue"/>'s own remarks describe for other field types stored the same
    /// way. A bare `is string`/`as string` check against a reloaded JsonElement fails silently,
    /// not with an exception, so this is the only safe way to read one back.
    /// </summary>
    public static string? ReadStringFieldValue(IReadOnlyDictionary<string, object?> fieldValues, string fieldKey) =>
        fieldValues.GetValueOrDefault(fieldKey) switch
        {
            string { Length: > 0 } value => value,
            JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString(),
            _ => null
        };

    /// <summary>
    /// Runs every <c>bulk-dataset-ingest</c>/<c>bulk-dataset-materialize</c> onEnter action on the
    /// stage <paramref name="cursor"/> just landed on, in declared order, and returns the
    /// FieldValues delta they produced — the caller merges this into the same field-value set it
    /// hands <see cref="ExecuteOnEnterSupportSystemActions"/> right afterwards. Unlike a
    /// support-system-call, both action types here talk only to host-local infrastructure (an
    /// already-fetched file, an in-process dataset store) — no external round trip — so they
    /// execute synchronously and resolve within this same call, never as a tracked pending
    /// invocation. Deliberately called before <see cref="ExecuteOnEnterSupportSystemActions"/> at
    /// every call site: a <c>bulk-dataset-materialize</c> action's whole purpose is to refresh a
    /// file field before that same stage's own <c>support-system-call</c> action reads it on a
    /// resubmission loop (see docs/guides/bulk-data-review.md).
    /// </summary>
    public Dictionary<string, object?> ExecuteOnEnterBulkDatasetActions(
        string instanceId,
        ServiceBlueprint definition,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor)
    {
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == cursor.CurrentNodeKey);
        if (stage?.Actions is not { Count: > 0 } actions)
        {
            return updates;
        }

        var workingFieldValues = new Dictionary<string, object?>(fieldValues, StringComparer.Ordinal);

        foreach (var action in actions)
        {
            if (action.Timing != ActionTiming.OnEnter)
            {
                continue;
            }

            IReadOnlyDictionary<string, object?> actionUpdates;
            if (string.Equals(action.Type, BulkDataActionTypes.BulkDatasetMaterialize, StringComparison.Ordinal))
            {
                actionUpdates = TryExecuteBulkDatasetMaterialize(instanceId, workingFieldValues, cursor, action);
            }
            else if (string.Equals(action.Type, BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
            {
                actionUpdates = TryExecuteBulkDatasetIngest(instanceId, workingFieldValues, cursor, action);
            }
            else
            {
                continue;
            }

            foreach (var (key, value) in actionUpdates)
            {
                updates[key] = value;
                workingFieldValues[key] = value;
            }
        }

        return updates;
    }

    /// <summary>
    /// Reconstructs the dataset named by this action's own <c>datasetIdField</c> and writes it
    /// into <c>targetFileField</c>. A safe no-op (returns no updates) when <c>datasetIdField</c>
    /// has no value yet — the expected case the first time this stage is entered, before
    /// anything's been ingested; the original upload already sitting in <c>targetFileField</c>
    /// goes through untouched.
    /// </summary>
    private IReadOnlyDictionary<string, object?> TryExecuteBulkDatasetMaterialize(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
        var targetFileField = action.Parameters["targetFileField"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(datasetIdField) || string.IsNullOrWhiteSpace(targetFileField))
        {
            MaterializeMissingFields(logger, cursor.CurrentNodeKey);
            return NoUpdates;
        }

        // A freshly-ingested datasetId survives this request as its original CLR string; one
        // reloaded from a JSON-backed store (e.g. UmbracoServiceRequestStore) comes back as a
        // boxed JsonElement instead (no custom converter on FieldValues — see GetDisplayValue's
        // own remarks on the same shape for file-upload/checkboxlist fields). An `is not string`
        // check against a JsonElement fails silently, misreading a genuinely-set datasetId as "no
        // value yet" and always falling back to the safe no-op — the original, uncorrected upload
        // materialized on every resubmit, forever. Found live: a real Playwright walkthrough where
        // a saved correction never once reached the downstream support system on resubmission.
        var datasetId = ReadStringFieldValue(fieldValues, datasetIdField);
        if (string.IsNullOrEmpty(datasetId))
        {
            return NoUpdates;
        }

        if (bulkDatasetStore is null)
        {
            NoStore(logger, "bulk-dataset-materialize", cursor.CurrentNodeKey);
            return NoUpdates;
        }

        ServiceRequestFileReference materialized;
        try
        {
            materialized = bulkDatasetStore
                .MaterializeAsync(instanceId, datasetId, targetFileField, $"{targetFileField}.csv", sanitizeForHumanExport: false)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MaterializeFailed(logger, ex, datasetId, cursor.CursorId);
            return NoUpdates;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { [targetFileField] = materialized };
    }

    /// <summary>
    /// Parses this action's <c>sourceFileField</c> against its declared <c>columns</c> into a
    /// fresh dataset via <see cref="IBulkDatasetStore.IngestAsync"/>, and returns the resulting
    /// dataset id plus any declared summary counts as field-value updates.
    /// </summary>
    private IReadOnlyDictionary<string, object?> TryExecuteBulkDatasetIngest(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var sourceFileField = action.Parameters["sourceFileField"]?.GetValue<string>();
        var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourceFileField) || string.IsNullOrWhiteSpace(datasetIdField))
        {
            IngestMissingFields(logger, cursor.CurrentNodeKey);
            return NoUpdates;
        }

        if (bulkDatasetStore is null)
        {
            NoStore(logger, "bulk-dataset-ingest", cursor.CurrentNodeKey);
            return NoUpdates;
        }

        var sourceFile = ServiceRequestFileReference.FromFieldValue(fieldValues.GetValueOrDefault(sourceFileField));
        if (sourceFile is null)
        {
            IngestSourceHasNoFile(logger, cursor.CurrentNodeKey, sourceFileField);
            return NoUpdates;
        }

        var columns = ParseBulkDatasetColumns(action);
        if (columns.Count == 0)
        {
            IngestNoColumns(logger, cursor.CurrentNodeKey);
            return NoUpdates;
        }

        BulkDatasetIngestResult result;
        try
        {
            result = bulkDatasetStore.IngestAsync(instanceId, sourceFile, columns).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            IngestThrew(logger, ex, sourceFileField, cursor.CursorId);
            return NoUpdates;
        }

        if (!result.Succeeded)
        {
            IngestFailed(logger, sourceFileField, cursor.CursorId, result.FailureReason);
            return NoUpdates;
        }

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal) { [datasetIdField] = result.DatasetId };
        AddDeclaredCountUpdate(action, "errorCountField", result.Summary!.ErrorRowCount, updates);
        AddDeclaredCountUpdate(action, "warningCountField", result.Summary.WarningRowCount, updates);
        AddDeclaredCountUpdate(action, "acceptedCountField", result.Summary.AcceptedRowCount, updates);
        // Always 0 immediately after a fresh ingest (a brand-new dataset starts with every row's
        // CurrentValues equal to its own OriginalValues) — see docs/guides/bulk-data-review.md's
        // sync-state section and SyncBulkDatasetSyncState, which is what keeps this field correct
        // for the rest of the stage's dwell, as corrections land outside of another ingest.
        AddDeclaredCountUpdate(action, "dirtyCountField", result.Summary.DirtyRowCount, updates);

        return updates;
    }

    private static void AddDeclaredCountUpdate(
        ActionDefinition action, string paramName, int value, Dictionary<string, object?> updates)
    {
        var fieldKey = action.Parameters[paramName]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fieldKey))
        {
            // decimal, not int: CalculationEvaluator.ValuesEqual only coerces when BOTH sides of
            // "=" are decimal — a numeric literal in a showWhen/calculation expression parses as
            // decimal, so a plain boxed int here would silently compare unequal to it even when
            // numerically equal (found live: njf-contributions.json's "Accept and finish" route,
            // showWhen: "contributionsErrorCount = 0", never became visible even once the count
            // genuinely reached zero). Matches ToFieldValues' own JsonValue-decimal handling —
            // "decimal for numbers in FieldValues" is this engine's established convention, not
            // something to work around per call site.
            updates[fieldKey] = (decimal)value;
        }
    }

    private static List<BulkDatasetColumnDescriptor> ParseBulkDatasetColumns(ActionDefinition action)
    {
        var columns = new List<BulkDatasetColumnDescriptor>();
        foreach (var columnNode in action.Parameters["columns"]?.AsArray() ?? [])
        {
            var column = columnNode?.AsObject();
            var key = column?["key"]?.GetValue<string>();
            var title = column?["title"]?.GetValue<string>();
            var roleValue = column?["role"]?.GetValue<string>();
            var valueKindValue = column?["valueKind"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(title)
                || !Enum.TryParse<BulkDatasetColumnRole>(roleValue, out var role)
                || !Enum.TryParse<ComponentPropertyValueKind>(valueKindValue, out var valueKind))
            {
                continue;
            }

            columns.Add(new BulkDatasetColumnDescriptor
            {
                Key = key,
                Title = title,
                ValueKind = valueKind,
                Format = column?["format"]?.GetValue<string>(),
                Role = role,
                Visible = column?["visible"]?.GetValue<bool>() ?? true,
                Editable = column?["editable"]?.GetValue<bool>() ?? false,
            });
        }

        return columns;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-dataset-materialize action on stage '{Stage}' is missing datasetIdField/targetFileField; skipped.")]
    private static partial void MaterializeMissingFields(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No IBulkDatasetStore registered; {Action} action on stage '{Stage}' skipped.")]
    private static partial void NoStore(ILogger logger, string action, string stage);

    [LoggerMessage(Level = LogLevel.Error, Message = "bulk-dataset-materialize failed for dataset '{DatasetId}' on cursor '{Cursor}'.")]
    private static partial void MaterializeFailed(ILogger logger, Exception exception, string datasetId, string cursor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-dataset-ingest action on stage '{Stage}' is missing sourceFileField/datasetIdField; skipped.")]
    private static partial void IngestMissingFields(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-dataset-ingest action on stage '{Stage}' references field '{Field}', which has no file value; skipped.")]
    private static partial void IngestSourceHasNoFile(ILogger logger, string stage, string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-dataset-ingest action on stage '{Stage}' declares no valid columns; skipped.")]
    private static partial void IngestNoColumns(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Error, Message = "bulk-dataset-ingest failed for field '{Field}' on cursor '{Cursor}'.")]
    private static partial void IngestThrew(ILogger logger, Exception exception, string field, string cursor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "bulk-dataset-ingest failed for field '{Field}' on cursor '{Cursor}': {Reason}")]
    private static partial void IngestFailed(ILogger logger, string field, string cursor, string? reason);
}
