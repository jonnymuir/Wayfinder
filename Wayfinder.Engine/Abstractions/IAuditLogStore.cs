namespace Wayfinder.Engine.Abstractions;

public enum AuditEventSeverity { Debug, Info, Warning, Error }

public enum AuditEventType
{
    Created,
    Transition,
    PickedUp,
    PutBack,

    /// <summary>Reserved for a future manager-initiated reassignment — not emitted by anything
    /// in this version. See docs/guides/work-allocation.md's reassignment seam.</summary>
    Reassigned
}

/// <summary>
/// One append-only entry in an instance's audit trail — who did what, when, plus the errors,
/// warnings, and debug detail a system admin or auditor needs to find later. See
/// docs/guides/work-allocation.md.
/// </summary>
public sealed record AuditEvent
{
    public required string EventId { get; init; }

    public required string InstanceId { get; init; }

    /// <summary>Null for an instance-level event with no single cursor to attribute it to.</summary>
    public string? CursorId { get; init; }

    public required AuditEventType EventType { get; init; }

    /// <summary>The userId that caused this event, or <c>"system"</c> for one the engine itself
    /// generated with no human actor (e.g. a scheduled sweep, not a support-system poll/webhook
    /// resolution — that always recurses through <c>Advance</c> attributed to the instance's own
    /// user, the same as every other call reaching that method).</summary>
    public required string Actor { get; init; }

    public string? FromStageKey { get; init; }

    public string? ToStageKey { get; init; }

    public string? Action { get; init; }

    public string? Detail { get; init; }

    public required AuditEventSeverity Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// The toolkit's extension point for the audit trail — mirrors <see cref="IServiceRequestFileStorage"/>'s
/// "engine defines interface, host implements storage" shape. <see cref="Services.ProcessManagerEngine"/>
/// ships a default in-memory implementation; a host wanting events to survive a restart, or to be
/// queryable by a real admin/auditor tool, implements this against its own persistence instead.
///
/// Deliberately untouched by <c>ProcessManagerEngine.Reset</c>/<c>ResetAll</c> — an audit trail
/// outliving the record it describes is the point, not an oversight; a host wanting log cleanup on
/// reset does so itself, against its own store.
/// </summary>
public interface IAuditLogStore
{
    /// <summary>Appends one event. Never mutates or removes an existing one.</summary>
    void Record(AuditEvent auditEvent);

    /// <summary>Every event recorded for one instance, oldest first.</summary>
    IReadOnlyList<AuditEvent> GetByInstance(string instanceId);

    /// <summary>Queries across every instance, newest first, with optional filters — every
    /// parameter left null/default matches everything.</summary>
    IReadOnlyList<AuditEvent> Query(
        string? instanceId = null,
        string? actor = null,
        AuditEventSeverity? minimumSeverity = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int pageIndex = 0,
        int pageSize = 50);
}
