namespace Wayfinder.Models.ServiceDesign.BulkData;

/// <summary>
/// <see cref="ActionDefinition.Type"/> conventions for bulk data review — see
/// docs/guides/bulk-data-review.md. Both reuse <c>ActionDefinition.Parameters</c> the same way
/// <see cref="SupportSystems.SupportSystemActionTypes.SupportSystemCall"/> already does; the
/// engine's execution of them (<c>Wayfinder.Engine</c>) is a later phase's job, not this one.
/// </summary>
public static class BulkDataActionTypes
{
    /// <summary>
    /// An <c>onEnter</c> action that parses a file-upload field's content into an indexed,
    /// pageable dataset via <c>Abstractions.IBulkDatasetStore</c>. Expected
    /// <c>ActionDefinition.Parameters</c>: <c>sourceFileField</c> (field-ref to the file to
    /// ingest), <c>datasetIdField</c> (field-ref the freshly-minted dataset id is written into —
    /// the single identifier a later <c>bulk-dataset-materialize</c> action or a
    /// <c>BulkDataReviewComponent</c> binds to), <c>columns</c> (array of
    /// <see cref="BulkDatasetColumnDescriptor"/>, one per CSV column), and optionally
    /// <c>errorCountField</c>/<c>warningCountField</c>/<c>acceptedCountField</c> (field-refs the
    /// ingest result's summary counts are written into, so ordinary calculation rules and route
    /// triggers can react to them without touching the dataset store directly).
    /// </summary>
    public const string BulkDatasetIngest = "bulk-dataset-ingest";

    /// <summary>
    /// An <c>onEnter</c> action — typically on the same stage a loop's <c>support-system-call</c>
    /// re-runs on, ordered before it — that reconstructs the full source CSV (original rows with
    /// any corrections overlaid, same column order as ingested) and writes it back into a file
    /// field — never a partial file, since the external system's own upload contract can't
    /// change. A no-op (leaves the target field untouched) when its <c>datasetIdField</c> has no
    /// value yet — the expected case the very first time a loop's stage is entered, before
    /// anything has been ingested. Expected <c>ActionDefinition.Parameters</c>:
    /// <c>datasetIdField</c> (field-ref identifying which dataset to materialize — must match the
    /// <c>bulk-dataset-ingest</c> action's own <c>datasetIdField</c> that produced it) and
    /// <c>targetFileField</c> (field-ref the materialized file is written into).
    /// </summary>
    public const string BulkDatasetMaterialize = "bulk-dataset-materialize";
}
