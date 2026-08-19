namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// A queue work item's status, derived by <see cref="Services.ProcessManagerEngine.GetQueueWorkItems"/>
/// from the actor-relative <c>AccessibleWorkItem</c> it was built from — never independently set.
/// A row that is none of these (the actor lacks permission to act in that queue at all, or every
/// outgoing route is <c>showWhen</c>-hidden — genuinely zero eligible routes, not merely zero
/// available ones) has no <see cref="QueueWorkItemStatus"/> at all and stays invisible under every
/// filter, exactly as it always has.
/// </summary>
public enum QueueWorkItemStatus
{
    /// <summary>Has at least one available action the caller may submit right now.</summary>
    Actionable,

    /// <summary>Nothing to do *yet* — the actor's own cursor is parked at a join gateway, waiting
    /// on another queue. See the historical note this replaced, below.</summary>
    Waiting,

    /// <summary>
    /// Genuinely resolved: no outbound routes, rendered as a confirmation panel — the same "is
    /// this actually terminal" check <c>IsTerminalInstance</c> uses for <c>GetCurrentOrStartFresh</c>
    /// (see docs/guides/request-concurrency.md), not merely "has no available actions" (which is
    /// also true for a permission gap or a hidden route — neither is completion).
    /// </summary>
    Done,

    /// <summary>
    /// Eligible routes exist here — the actor has permission to act in this queue and at least one
    /// route isn't <c>showWhen</c>-hidden — but this specific row isn't individually assigned to
    /// them yet. Only ever produced for a queue declaring <c>QueueDefinition.AssignmentPolicy</c>
    /// (see docs/guides/team-assignment.md): a "team-tray" row nobody has picked up. Distinct from
    /// today's legacy queues, where "eligible" and "actionable" have always been the same thing.
    /// <c>AvailableActions</c> is always empty for these — pick it up via <c>PickupWorkItem</c> to
    /// move to <see cref="Actionable"/>.
    /// </summary>
    Unassigned,
}

/// <summary>
/// Per-cursor (or, for a team-owned queue, per-<c>QueueAssignment</c>) pickup/ownership state — a
/// genuinely different axis from <see cref="QueueWorkItemStatus"/> (status answers "what can be
/// done"; pickup state answers "who's doing it"). Non-null only for a genuine shared-pool row on a
/// queue whose actor profile isn't restricted to its own instances: for a legacy queue, that means
/// <c>Status == Actionable</c> (not picked up vs. picked up by me — picking it up is what makes it
/// actionable in the first place there); for a team-owned queue, both
/// <see cref="QueueWorkItemStatus.Unassigned"/> (not picked up, sitting in the team tray) and
/// <c>Actionable</c> (already picked up, by me) apply. Null everywhere else — a
/// <see cref="QueueWorkItemStatus.Waiting"/>/<see cref="QueueWorkItemStatus.Done"/> row, an
/// owner-restricted instance, or an "assign-to-initiator" queue (nothing to pick up — it's always
/// already owned) has nothing to pick up. A row held by someone else — an individual on a legacy
/// queue, or a different team member on a team-owned one — never produces a row at all for anyone
/// but its holder (see <c>ProcessManagerEngine.FindAccessibleWorkItems</c>'s ownership filter), so
/// there is no third "picked up by someone else" value here to enumerate. See
/// docs/guides/work-allocation.md and docs/guides/team-assignment.md — and note this is unrelated
/// to <c>IQueueCapabilitiesProvider</c>'s own pre-existing, differently-scoped use of the word
/// "capability".
/// </summary>
public enum QueueWorkItemPickupState
{
    NotPickedUp,
    PickedUpByMe,
}

/// <summary>Ordering for <see cref="Services.ProcessManagerEngine.GetQueueWorkItems"/> — every
/// non-<see cref="Default"/> value still ends in an <c>InstanceId</c> tiebreak internally, since
/// <c>IServiceRequestStore.GetAll()</c> gives no ordering guarantee of its own and a paged list
/// needs a deterministic order for rows not to drift between pages across requests.</summary>
public enum QueueWorkListSort
{
    /// <summary>Today's fixed order: blueprint display name, then stage display name, then instance id.</summary>
    Default,
    CreatedAtNewestFirst,
    CreatedAtOldestFirst,
    UpdatedAtNewestFirst,
    UpdatedAtOldestFirst,
}

public record QueueWorkListEnvelope
{
    public IReadOnlyList<QueueWorkItem> Items { get; init; } = [];

    public DateTimeOffset ServerTimeUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The page index this envelope's <see cref="Items"/> represents (0-based).</summary>
    public int PageIndex { get; init; }

    /// <summary>The page size this envelope's <see cref="Items"/> was built with.</summary>
    public int PageSize { get; init; }

    /// <summary>
    /// How many items matched the requested status/search filter in total, independent of paging
    /// — a caller needs this to render "page 2 of N" or a "showing X of Y" count.
    /// </summary>
    public int TotalMatchingCount { get; init; }
}

public record QueueWorkItem
{
    public string InstanceId { get; init; } = "";

    public string BlueprintKey { get; init; } = "";

    public string BlueprintDisplayName { get; init; } = "";

    public string StageKey { get; init; } = "";

    public string StateDisplayName { get; init; } = "";

    public string? QueueName { get; init; }

    /// <summary>The cursor this row was built from — <see cref="RequestCursor.PrimaryCursorId"/>
    /// for an instance that hasn't crossed its first gateway yet. What <c>PickupWorkItem</c>/
    /// <c>PutbackWorkItem</c> take as their own <c>cursorId</c> argument.</summary>
    public string CursorId { get; init; } = "";

    /// <summary>See <see cref="QueueWorkItemPickupState"/>. Null for a row with nothing to pick up.</summary>
    public QueueWorkItemPickupState? PickupState { get; init; }

    public string TenantId { get; init; } = "";

    public string UserId { get; init; } = "";

    public int StateVersion { get; init; }

    public IReadOnlyList<ServiceRequestAction> AvailableActions { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>See <see cref="QueueWorkItemStatus"/> — the source of truth <see cref="IsWaiting"/> is derived from.</summary>
    public QueueWorkItemStatus Status { get; init; }

    /// <summary>
    /// True when this item is in the actor's queue but has nothing for them to do *yet* — their
    /// own cursor is parked at a join gateway, waiting on another queue (another team, or an
    /// automation queue waiting on a support system — see docs/guides/support-systems.md).
    /// <see cref="AvailableActions"/> is always empty for these.
    ///
    /// A worklist that only ever showed actionable items made an application waiting on a support
    /// system *disappear* from the caseworker's queue entirely, with no way back to it but a
    /// remembered URL — found by actually walking the juggling-licence "send to insurer" journey.
    /// The citizen has always had a real wait screen for exactly this state; a backstage actor
    /// needs the same visibility, so "what am I waiting on" belongs in the worklist alongside
    /// "what can I act on", flagged so a host can render it distinctly rather than as a dead row
    /// with no buttons.
    /// </summary>
    public bool IsWaiting => Status == QueueWorkItemStatus.Waiting;
}
