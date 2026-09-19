namespace Wayfinder.Models.ServiceDesign;

/// <summary>Ordering for <c>ProcessManagerEngine.SearchInstancesForAdmin</c> — every value ends in
/// an <c>InstanceId</c> tiebreak internally, since <c>IServiceRequestStore.GetAll()</c> gives no
/// ordering guarantee of its own and a paged list needs a deterministic order for rows not to
/// drift between pages across requests.</summary>
public enum ServiceRequestAdminSort
{
    /// <summary>
    /// The default: staleness first — the instance nobody has touched for longest surfaces at the
    /// top, since that's the one most likely to be stuck (waiting on a support system that never
    /// called back, an abandoned form, a caseworker who never returned). Diagnosing exactly this
    /// kind of stuck instance is what this whole search surface exists for.
    /// </summary>
    UpdatedAtOldestFirst,
    UpdatedAtNewestFirst,
    CreatedAtOldestFirst,
    CreatedAtNewestFirst,
}

/// <summary>
/// Filters for <c>ProcessManagerEngine.SearchInstancesForAdmin</c>. Every filter is optional —
/// omitting all of them searches every instance across every tenant, deliberately: this is genuine
/// cross-tenant admin tooling (see <c>IProcessManager.GetAllInstances</c>'s own remarks), not a
/// tenant-scoped or actor-scoped view.
/// </summary>
public sealed record ServiceRequestAdminQuery
{
    public string? BlueprintKey { get; init; }

    public string? TenantId { get; init; }

    /// <summary>
    /// <see langword="false"/> (the default) hides already-aborted instances — an admin searching
    /// for what's currently stuck doesn't want a search cluttered with rows they, or a colleague,
    /// already dealt with. Set <see langword="true"/> to include them (e.g. auditing past aborts).
    /// </summary>
    public bool IncludeAborted { get; init; }

    /// <summary>Matches case-insensitively across instance id, blueprint display name, and every raw field value — the same convention <c>GetQueueWorkItems</c> already established.</summary>
    public string? SearchText { get; init; }

    public ServiceRequestAdminSort Sort { get; init; } = ServiceRequestAdminSort.UpdatedAtOldestFirst;

    public int PageIndex { get; init; }

    public int PageSize { get; init; } = 20;
}

public sealed record ServiceRequestAdminListEnvelope
{
    public IReadOnlyList<ServiceRequestAdminSummary> Items { get; init; } = [];

    public DateTimeOffset ServerTimeUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The page index this envelope's <see cref="Items"/> represents (0-based).</summary>
    public int PageIndex { get; init; }

    /// <summary>The page size this envelope's <see cref="Items"/> was built with.</summary>
    public int PageSize { get; init; }

    /// <summary>
    /// How many instances matched the requested filter in total, independent of paging — a caller
    /// needs this to render "page 2 of N" or a "showing X of Y" count.
    /// </summary>
    public int TotalMatchingCount { get; init; }
}

/// <summary>One row of a <c>ProcessManagerEngine.SearchInstancesForAdmin</c> result.</summary>
public sealed record ServiceRequestAdminSummary
{
    public string InstanceId { get; init; } = "";

    public string BlueprintKey { get; init; } = "";

    public string BlueprintDisplayName { get; init; } = "";

    public string TenantId { get; init; } = "";

    public string UserId { get; init; } = "";

    public string CurrentStage { get; init; } = "";

    public string CurrentStateDisplayName { get; init; } = "";

    /// <summary>True once the instance has reached a genuine terminal (confirmation) stage — same
    /// derivation <c>ServiceRequestSummary.IsCompleted</c> already uses, independent of
    /// <see cref="IsAborted"/> (an instance can be terminal without ever having been aborted, and
    /// vice versa — an admin can abort an in-progress instance too).</summary>
    public bool IsCompleted { get; init; }

    public bool IsAborted { get; init; }

    public DateTimeOffset? AbortedAt { get; init; }

    public string? AbortedReason { get; init; }

    public string? AbortedByUserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
