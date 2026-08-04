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
}
