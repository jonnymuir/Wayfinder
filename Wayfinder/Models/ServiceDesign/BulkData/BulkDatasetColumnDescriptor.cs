using System.Text.Json.Serialization;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Models.ServiceDesign.BulkData;

/// <summary>
/// What one column of a bulk dataset means to the review experience — see
/// <see cref="BulkDatasetColumnDescriptor.Role"/>. Exactly one column per dataset must be
/// <see cref="RowKey"/>: the column the external system is expected to echo back unchanged,
/// used to correlate a row across resubmission rounds (the real-world reason a bordereau
/// carries a client reference column). <see cref="ResponseError"/>/<see cref="ResponseWarning"/>
/// columns drive which rows count as "needing attention"; <see cref="ResponseMatchedId"/> is
/// enrichment the external system supplied, rendered read-only.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BulkDatasetColumnRole>))]
public enum BulkDatasetColumnRole
{
    /// <summary>The stable identifier for a row, expected to round-trip unchanged. Exactly one per dataset.</summary>
    RowKey,

    /// <summary>An ordinary business value — may be <c>Editable</c>.</summary>
    Data,

    /// <summary>An identifier the external system assigned/matched on ingest — read-only.</summary>
    ResponseMatchedId,

    /// <summary>Non-empty marks this row as needing attention. Read-only.</summary>
    ResponseError,

    /// <summary>Non-empty marks this row as needing attention (soft). Read-only.</summary>
    ResponseWarning,

    /// <summary>Present in the file but not shown or acted on.</summary>
    Ignored,
}

/// <summary>
/// Describes one column of a <c>bulk-dataset-ingest</c> action's source file — enough for
/// <see cref="Abstractions.IBulkDatasetStore"/> (in <c>Wayfinder.Engine</c>) to parse, index,
/// and know how to present each column, without the review component needing any column
/// configuration of its own (see docs/guides/bulk-data-review.md). Reuses
/// <see cref="ComponentPropertyValueKind"/> for <see cref="ValueKind"/> rather than the fuller
/// <see cref="ComponentPropertyDescriptor"/> shape: a bulk-dataset-ingest action's <c>columns</c>
/// parameter is authored directly as JSON inside <c>ActionDefinition.Parameters</c>, not
/// reflected against a CLR component instance the way a component's own properties are, so the
/// heavier descriptor's CLR-property-name plumbing (<see cref="ComponentPropertyDescriptor.Key"/>'s
/// <c>nameof</c> convention) doesn't apply here — only the small, closed value-kind vocabulary is
/// worth sharing.
/// </summary>
public sealed record BulkDatasetColumnDescriptor
{
    /// <summary>The literal CSV header this column binds to.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label for the review UI, e.g. "Monthly contribution".</summary>
    public required string Title { get; init; }

    public required ComponentPropertyValueKind ValueKind { get; init; }

    /// <summary>Semantic hint for the value's shape, e.g. <c>"currency"</c>, <c>"date"</c> — same vocabulary as <see cref="ComponentPropertyDescriptor.Format"/>.</summary>
    public string? Format { get; init; }

    public required BulkDatasetColumnRole Role { get; init; }

    /// <summary>Whether this column renders in the review UI at all. Defaults to true.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Whether a user may correct this column's value. Only meaningful when <see cref="Role"/> is <see cref="BulkDatasetColumnRole.Data"/> — ignored otherwise.</summary>
    public bool Editable { get; init; }
}
