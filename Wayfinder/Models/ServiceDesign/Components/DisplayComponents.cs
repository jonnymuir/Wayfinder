namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// A group of headline statistic tiles (label + value + qualifier), rendered read-only.
/// Values are resolved at render time from instance field values via each item's FieldKey,
/// so the host application controls the figures shown.
/// </summary>
public sealed record StatGroupComponent : Component
{
    /// <summary>Optional heading rendered above the tiles.</summary>
    public string? Title { get; init; }

    /// <summary>The statistic tiles in display order.</summary>
    public IReadOnlyList<StatItemDefinition> Items { get; init; } = Array.Empty<StatItemDefinition>();
}

/// <summary>
/// A single statistic tile within a <see cref="StatGroupComponent"/>.
/// </summary>
public sealed record StatItemDefinition
{
    /// <summary>Short label above the value (e.g. "DB pension").</summary>
    public string Label { get; init; } = "";

    /// <summary>Instance field key the displayed value is read from at render time.</summary>
    public string FieldKey { get; init; } = "";

    /// <summary>Qualifier text below the value (e.g. "a year, for life").</summary>
    public string? Qualifier { get; init; }

    /// <summary>Whether to render this tile with visual emphasis.</summary>
    public bool Emphasis { get; init; }
}

/// <summary>
/// Declarative chart bound to a calculation series. The server renders it statically
/// (bars, legend and an accessible data table); the live-form runtime re-renders it
/// as inputs change. Currently supported kind: "stacked-bar".
/// </summary>
public sealed record ChartComponent : Component
{
    /// <summary>Chart heading shown above the plot.</summary>
    public string? Title { get; init; }

    /// <summary>Chart kind. Currently "stacked-bar".</summary>
    public string Kind { get; init; } = "stacked-bar";

    /// <summary>Name of the calculation series supplying the rows.</summary>
    public string Series { get; init; } = "";

    /// <summary>Series column used for the x axis (typically the loop variable).</summary>
    public string X { get; init; } = "";

    /// <summary>Stacked bands in draw order (bottom first).</summary>
    public IReadOnlyList<ChartBand> Bands { get; init; } = Array.Empty<ChartBand>();

    /// <summary>Label interval on the x axis (e.g. 5 → label every 5th value). Default 5.</summary>
    public int XLabelEvery { get; init; } = 5;
}

/// <summary>One stacked band of a chart, bound to a series column.</summary>
public sealed record ChartBand
{
    /// <summary>Series column name supplying this band's values.</summary>
    public string Key { get; init; } = "";

    /// <summary>Legend label.</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Optional hex colour. When omitted the renderer assigns from its validated
    /// categorical palette in band order.
    /// </summary>
    public string? Color { get; init; }
}

/// <summary>
/// The bulk-data review surface (see docs/guides/bulk-data-review.md) — a paginated "only show me
/// what needs attention" card UI over a dataset a <c>bulk-dataset-ingest</c> action already
/// produced. Deliberately near-config-free: <see cref="DatasetIdField"/> is the one thing it
/// needs, the same field-ref a <c>bulk-dataset-materialize</c> action binds to — every column's
/// role/visibility/editability is already known to the store from ingest time, so this component
/// never re-declares the dataset's shape itself.
/// </summary>
public sealed record BulkDataReviewComponent : Component
{
    /// <summary>Optional heading rendered above the review UI.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Field-ref to the field a <c>bulk-dataset-ingest</c> action wrote its minted dataset id
    /// into — must match that action's own <c>datasetIdField</c> parameter.
    /// </summary>
    public string DatasetIdField { get; init; } = "";

    /// <summary>How many attention-rows to show per page. Default 20.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// Label shown when a row/the whole dataset matches what was last checked — null/empty falls
    /// back to "Synced". Deliberately generic wording by default: "checked" fits any revalidation
    /// scheme, not just a resubmit-a-file one (see <see cref="PendingLabel"/>).
    /// </summary>
    public string? SyncedLabel { get; init; }

    /// <summary>
    /// Label shown when a row/the whole dataset has changed since the last check — null/empty
    /// falls back to "Needs resubmission". This is genuinely per-service vocabulary: a file
    /// resubmission flow (njf-contributions.json sets this to "Pending resubmission") reads very
    /// differently from, say, a service that revalidates by re-running an internal check with no
    /// external file at all. See docs/guides/bulk-data-review.md's sync-state section.
    /// </summary>
    public string? PendingLabel { get; init; }

    /// <summary>
    /// The reference-point phrase composed into both the sync-status line and the "discard all
    /// pending changes" confirmation warning, e.g. "since the file was last checked" (the default)
    /// or "since the file was last submitted". Kept as one shared property, not two separate
    /// strings, so the two surfaces can never say something different from each other for the
    /// same blueprint.
    /// </summary>
    public string? SinceLabel { get; init; }
}
