namespace Wayfinder.Engine.Models;

/// <summary>
/// Durable, per-queue-key ownership record for a team-owned queue (<c>QueueDefinition.AssignmentPolicy</c>
/// declared) — established once the first time an instance's cursor lands in that queue key, looked
/// up (not re-derived) on every later cursor computation for the same key, left in place when the
/// cursor moves elsewhere so re-entry into the same key reuses it. Never used for a legacy queue
/// (no <c>AssignmentPolicy</c> declared) — see <see cref="RequestCursor.AssignedTo"/> for that case.
/// See docs/guides/team-assignment.md.
/// </summary>
public sealed record QueueAssignment
{
    public required string QueueKey { get; init; }

    /// <summary>The team that owns this queue — copied from <c>QueueDefinition.OwningTeamId</c> at establishment time.</summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// The individual currently holding this queue's work for this instance — null means it's
    /// either owned only by the team (a "team-tray" row nobody has picked up yet) or, for an
    /// "assign-to-initiator" queue, is never null once established.
    /// </summary>
    public string? AssignedUserId { get; init; }

    /// <summary>When <see cref="AssignedUserId"/> was set. Null iff <see cref="AssignedUserId"/> is null.</summary>
    public DateTimeOffset? AssignedAt { get; init; }

    /// <summary>When this queue's assignment record was first established for this instance.</summary>
    public required DateTimeOffset EstablishedAt { get; init; }
}
