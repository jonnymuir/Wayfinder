namespace Wayfinder.Engine.Models;

/// <summary>
/// Tracks a single active execution point within a multi-queue service request.
/// One cursor per active queue path; a split gateway creates multiple cursors,
/// a join gateway holds cursors until all required queues arrive, then releases.
/// </summary>
public record RequestCursor
{
    /// <summary>Stable identifier for this cursor within the instance.</summary>
    public string CursorId { get; init; } = "";

    /// <summary>The queue this cursor belongs to.</summary>
    public string QueueKey { get; init; } = "";

    /// <summary>The stage or gateway key where this cursor is currently positioned.</summary>
    public string CurrentNodeKey { get; init; } = "";

    /// <summary>True when this cursor is positioned at a gateway node rather than a stage.</summary>
    public bool IsAtGateway { get; init; }

    /// <summary>
    /// The action/trigger of the transition that most recently moved this cursor onto a gateway
    /// node, if any. Used by a Join gateway with more than one outgoing route to decide which
    /// route to release on — e.g. a cursor that arrived via "approve" releases the join's
    /// "approve"-triggered route, "reject" releases its "reject"-triggered route. Null/irrelevant
    /// once the cursor is on a stage.
    /// </summary>
    public string? ArrivedViaAction { get; init; }

    /// <summary>
    /// The userId currently holding this cursor's work, or null if unclaimed (or not a claimable
    /// row at all — see docs/guides/work-allocation.md). A claim is scoped to this cursor's dwell
    /// at its current node: cleared automatically the instant this cursor is consumed by a Split or
    /// Join gateway crossing, since both mint a brand-new <see cref="RequestCursor"/> rather than
    /// carrying this field forward — deliberate, not an oversight. Only <c>ClaimWorkItem</c>/
    /// <c>ClaimNextAvailableWorkItem</c> ever set this; only <c>ReleaseWorkItem</c> (or a future
    /// reassignment operation) ever clears/changes it. Never touched by <c>Advance</c> itself.
    /// </summary>
    public string? AssignedTo { get; init; }

    /// <summary>When <see cref="AssignedTo"/> was set. Null iff <see cref="AssignedTo"/> is null.</summary>
    public DateTimeOffset? AssignedAt { get; init; }

    /// <summary>
    /// Well-known cursor id for claiming an instance before it has crossed its first gateway — its
    /// own <c>Cursors</c> list is still empty at that point (<c>CreateNewInstance</c> only populates
    /// it the first time a gateway is crossed), yet a freshly created, unclaimed instance whose
    /// initial stage already sits in a shared queue is a real, durable state (njf-contributions'
    /// own "upload" stage, for instance), not a transient edge case. Never collides with a real
    /// cursor id, which is always a freshly minted <see cref="Guid"/>.
    /// </summary>
    public const string PrimaryCursorId = "$primary";
}
