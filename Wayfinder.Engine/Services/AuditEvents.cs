using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Services;

/// <summary>Builders for the audit events the engine records.</summary>
internal static class AuditEvents
{
    public static AuditEvent WorkItem(string instanceId, string actor, string cursorId, AuditEventType eventType, string detail) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        InstanceId = instanceId,
        CursorId = cursorId,
        EventType = eventType,
        Actor = actor,
        Detail = detail,
        Severity = AuditEventSeverity.Info,
        OccurredAt = DateTimeOffset.UtcNow
    };

    public static AuditEvent Transition(
        string instanceId, string actor, string? cursorId, string? fromStageKey, string? toStageKey, string? action, string? detail = null) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        InstanceId = instanceId,
        CursorId = cursorId,
        EventType = AuditEventType.Transition,
        Actor = actor,
        FromStageKey = fromStageKey,
        ToStageKey = toStageKey,
        Action = action,
        Detail = detail,
        Severity = AuditEventSeverity.Info,
        OccurredAt = DateTimeOffset.UtcNow
    };
}
