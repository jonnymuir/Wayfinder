using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// Which rows a <see cref="IBulkDatasetStore.GetRowsAsync"/> page should include, driven by each
/// row's <see cref="BulkDatasetRow.HasError"/>/<see cref="BulkDatasetRow.HasWarning"/> — computed
/// once at ingest from the file's own <see cref="BulkDatasetColumnRole.ResponseError"/>/
/// <see cref="BulkDatasetColumnRole.ResponseWarning"/> columns, never re-derived per query.
/// </summary>
public enum BulkDatasetRowFilter
{
    /// <summary>Has an error and/or a warning — the review UI's default view.</summary>
    NeedsAttention,
    HasError,
    HasWarningOnly,
    Accepted,
    All,
}

/// <summary>
/// One field a user corrected on a row — kept as an attributable overlay on top of the row's
/// immutable <see cref="BulkDatasetRow.OriginalValues"/>, never an in-place overwrite. This is
/// the whole of the feature's "audit trail as data" tamper-evidence design (see
/// docs/guides/bulk-data-review.md): what an external system originally said is always
/// recoverable, and every human edit is attributable to who made it and when.
/// </summary>
public sealed record BulkDatasetRowCorrection
{
    public required string ColumnKey { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public required string CorrectedBy { get; init; }
    public required DateTimeOffset CorrectedAt { get; init; }
}

/// <summary>
/// One row of an ingested dataset. <see cref="OriginalValues"/> is exactly what was ingested,
/// immutable; <see cref="CurrentValues"/> is that overlaid with any <see cref="Corrections"/> —
/// what a review UI should display as the row's working value, and what
/// <see cref="IBulkDatasetStore.MaterializeAsync"/> writes out. <see cref="StructuralIssue"/> is
/// set instead of the row correlating cleanly against <see cref="BulkDatasetColumnRole.RowKey"/>
/// (a missing/duplicate key, or a column count that doesn't match the declared schema) — the file
/// didn't fit its own declared shape, treated as an attention-worthy row rather than a fatal
/// ingest error, since a single malformed line in an otherwise-good file shouldn't block the rest
/// of it (see docs/guides/bulk-data-review.md's "never trust the file's own structure" principle).
/// </summary>
public sealed record BulkDatasetRow
{
    public required string RowKey { get; init; }
    public required int RowIndex { get; init; }
    public required IReadOnlyDictionary<string, string?> OriginalValues { get; init; }
    public required IReadOnlyDictionary<string, string?> CurrentValues { get; init; }
    public IReadOnlyList<BulkDatasetRowCorrection> Corrections { get; init; } = [];
    public required bool HasError { get; init; }
    public required bool HasWarning { get; init; }
    public string? StructuralIssue { get; init; }
}

/// <summary>
/// One page of rows matching a <see cref="BulkDatasetRowFilter"/> — a
/// <see cref="IBulkDatasetStore.GetRowsAsync"/> caller only ever asks for one page at a time; the
/// full dataset is never assembled for a single response. See docs/guides/bulk-data-review.md's
/// performance principles.
/// </summary>
public sealed record BulkDatasetRowPage
{
    public required IReadOnlyList<BulkDatasetRow> Rows { get; init; }
    public required int PageIndex { get; init; }
    public required int PageSize { get; init; }
    public required int TotalMatchingRowCount { get; init; }
}

/// <summary>Row-count summary for a dataset — what a <c>bulk-dataset-ingest</c> action's own count-output params (see <c>BulkData.BulkDataActionTypes</c>) get their values from.</summary>
public sealed record BulkDatasetSummary
{
    public required string DatasetId { get; init; }
    public required int TotalRowCount { get; init; }
    public required int ErrorRowCount { get; init; }
    public required int WarningRowCount { get; init; }

    /// <summary>Rows with neither an error nor a warning — <see cref="TotalRowCount"/> minus every row counted in <see cref="ErrorRowCount"/> or <see cref="WarningRowCount"/> (a row with both counts once, in each).</summary>
    public required int AcceptedRowCount { get; init; }

    /// <summary>
    /// The column schema this dataset was ingested against — a row's own <c>OriginalValues</c>/
    /// <c>CurrentValues</c> are just key→value dictionaries, with no per-column title/role/
    /// editability of their own, so a client rendering rows (see <c>wayfinder-bulk-data-review.js</c>)
    /// needs this to know what to label, show, or let a user correct.
    /// </summary>
    public required IReadOnlyList<BulkDatasetColumnDescriptor> Columns { get; init; }
}

/// <summary>
/// Result of a <see cref="IBulkDatasetStore.IngestAsync"/> call. Ingest can fail outright — an
/// oversized file, a row/cell-length cap breached, or the file missing a column the schema
/// declares — as a normal result, never an unhandled exception (see docs/guides/bulk-data-review.md's
/// performance principles: "a clean diagnostic, not a crash"). A whole-file failure is
/// deliberately distinct from a single malformed row (<see cref="BulkDatasetRow.StructuralIssue"/>)
/// — the former means nothing usable could be indexed at all; the latter means one bad line
/// didn't block the rest of a mostly-good file.
/// </summary>
public sealed record BulkDatasetIngestResult
{
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public string? DatasetId { get; init; }
    public BulkDatasetSummary? Summary { get; init; }

    public static BulkDatasetIngestResult Success(BulkDatasetSummary summary) => new()
    {
        Succeeded = true,
        DatasetId = summary.DatasetId,
        Summary = summary,
    };

    public static BulkDatasetIngestResult Failure(string reason) => new()
    {
        Succeeded = false,
        FailureReason = reason,
    };
}

/// <summary>
/// The toolkit's extension point for bulk, row-level data (see docs/guides/bulk-data-review.md) —
/// the same "engine defines the interface, host implements it" seam as
/// <see cref="IServiceRequestFileStorage"/>/<see cref="ISupportSystemClient"/>. A host
/// implementation typically holds its own <see cref="IServiceRequestFileStorage"/> reference and
/// resolves <see cref="ServiceRequestFileReference"/>s itself — <see cref="Services.ProcessManagerEngine"/>
/// never touches file bytes, the same invariant support-system clients already keep.
///
/// Every method takes <paramref name="instanceId"/>/<c>instanceId</c> and independently verifies
/// it owns the dataset — a minted, unguessable <c>DatasetId</c> is defence in depth, not the only
/// check, since this store holds member-level PII unlike the largely opaque single-file case
/// <see cref="IServiceRequestFileStorage"/> was first written for. Implementations should throw
/// <see cref="UnauthorizedAccessException"/> when a dataset exists but belongs to a different
/// instance — distinct from a dataset simply not existing (a normal <see langword="null"/>/no-op
/// case, e.g. a stale id from an old session).
/// </summary>
public interface IBulkDatasetStore
{
    /// <summary>
    /// Parses <paramref name="sourceFile"/> against <paramref name="columns"/> into a fresh,
    /// indexed dataset scoped to <paramref name="instanceId"/>. Always mints a new
    /// <see cref="BulkDatasetIngestResult.DatasetId"/> — onEnter actions fire exactly once per
    /// stage arrival (never on a mere poll/refresh), so there's no re-ingest-the-same-file case
    /// worth optimising for; see docs/guides/bulk-data-review.md.
    /// </summary>
    Task<BulkDatasetIngestResult> IngestAsync(
        string instanceId,
        ServiceRequestFileReference sourceFile,
        IReadOnlyList<BulkDatasetColumnDescriptor> columns,
        CancellationToken ct = default);

    Task<BulkDatasetSummary?> GetSummaryAsync(string instanceId, string datasetId, CancellationToken ct = default);

    Task<BulkDatasetRowPage?> GetRowsAsync(
        string instanceId,
        string datasetId,
        BulkDatasetRowFilter filter,
        int pageIndex,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Overlays corrections onto one row. Only columns declared <c>Role: Data</c> and
    /// <c>Editable: true</c> in the dataset's own schema may be corrected — enforced here, not
    /// just by a client UI, since a client is never trusted input (see docs/guides/bulk-data-review.md's
    /// "secure by design" principle). Throws <see cref="ArgumentException"/> for a column that
    /// isn't editable, <see cref="KeyNotFoundException"/> for an unknown <paramref name="rowKey"/>.
    /// </summary>
    Task ApplyCorrectionAsync(
        string instanceId,
        string datasetId,
        string rowKey,
        IReadOnlyDictionary<string, string?> correctedValues,
        string correctedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Reconstructs the full dataset (original rows with corrections overlaid, same column order
    /// as ingested) as a CSV, saves it via the store's own file storage under
    /// <paramref name="targetFieldKey"/>, and returns the resulting reference —
    /// <see cref="Services.ProcessManagerEngine"/> only ever merges the reference into
    /// <c>FieldValues</c>, it never touches the bytes itself. <paramref name="sanitizeForHumanExport"/>
    /// neutralizes CSV formula injection (a cell starting with <c>=</c>, <c>+</c>, <c>-</c>,
    /// <c>@</c>, tab, or CR) — set only for a human-facing download, never for a resubmission to
    /// the external system, which would corrupt real data a machine parser depends on.
    /// </summary>
    Task<ServiceRequestFileReference> MaterializeAsync(
        string instanceId,
        string datasetId,
        string targetFieldKey,
        string fileName,
        bool sanitizeForHumanExport,
        CancellationToken ct = default);
}
