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
    /// ingest), <c>columns</c> (array of <see cref="BulkDatasetColumnDescriptor"/>, one per CSV
    /// column), and optionally <c>errorCountField</c>/<c>warningCountField</c>/
    /// <c>acceptedCountField</c> (field-refs the ingest result's summary counts are written into,
    /// so ordinary calculation rules and route triggers can react to them without touching the
    /// dataset store directly).
    /// </summary>
    public const string BulkDatasetIngest = "bulk-dataset-ingest";

    /// <summary>
    /// A route action that reconstructs the full source CSV (original rows with any corrections
    /// overlaid, same column order as ingested) and writes it back into a file field — never a
    /// partial file, since the external system's own upload contract can't change. Expected
    /// <c>ActionDefinition.Parameters</c>: <c>sourceFileField</c> (identifies which dataset —
    /// must match the <c>bulk-dataset-ingest</c> action's own <c>sourceFileField</c> for this
    /// stage) and <c>targetFileField</c> (field-ref the materialized file is written into).
    /// </summary>
    public const string BulkDatasetMaterialize = "bulk-dataset-materialize";
}
