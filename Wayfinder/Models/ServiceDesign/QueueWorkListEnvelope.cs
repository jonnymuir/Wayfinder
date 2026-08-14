namespace Wayfinder.Models.ServiceDesign;

public record QueueWorkListEnvelope
{
    public IReadOnlyList<QueueWorkItem> Items { get; init; } = [];

    public DateTimeOffset ServerTimeUtc { get; init; } = DateTimeOffset.UtcNow;
}

public record QueueWorkItem
{
    public string InstanceId { get; init; } = "";

    public string BlueprintKey { get; init; } = "";

    public string BlueprintDisplayName { get; init; } = "";

    public string StageKey { get; init; } = "";

    public string StateDisplayName { get; init; } = "";

    public string? QueueName { get; init; }

    public string TenantId { get; init; } = "";

    public string UserId { get; init; } = "";

    public int StateVersion { get; init; }

    public IReadOnlyList<ServiceRequestAction> AvailableActions { get; init; } = [];

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
    public bool IsWaiting { get; init; }
}
