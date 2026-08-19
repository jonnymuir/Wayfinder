using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Stores;

/// <summary>
/// Default <see cref="IAuditLogStore"/> — process-lifetime only, matching every other in-memory
/// default in this toolkit. Append-only: a <see cref="List{T}"/> guarded by a lock, since events
/// are written far more often than queried and a lock's simplicity is worth more here than a
/// lock-free structure would buy.
/// </summary>
public sealed class InMemoryAuditLogStore : IAuditLogStore
{
    private readonly object _lock = new();
    private readonly List<AuditEvent> _events = [];

    public void Record(AuditEvent auditEvent)
    {
        lock (_lock)
        {
            _events.Add(auditEvent);
        }
    }

    public IReadOnlyList<AuditEvent> GetByInstance(string instanceId)
    {
        lock (_lock)
        {
            return _events
                .Where(e => string.Equals(e.InstanceId, instanceId, StringComparison.Ordinal))
                .OrderBy(e => e.OccurredAt)
                .ToArray();
        }
    }

    public IReadOnlyList<AuditEvent> Query(
        string? instanceId = null,
        string? actor = null,
        AuditEventSeverity? minimumSeverity = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int pageIndex = 0,
        int pageSize = 50)
    {
        lock (_lock)
        {
            var matched = _events.Where(e =>
                (instanceId is null || string.Equals(e.InstanceId, instanceId, StringComparison.Ordinal))
                && (actor is null || string.Equals(e.Actor, actor, StringComparison.Ordinal))
                && (minimumSeverity is null || e.Severity >= minimumSeverity.Value)
                && (fromUtc is null || e.OccurredAt >= fromUtc.Value)
                && (toUtc is null || e.OccurredAt <= toUtc.Value));

            return matched
                .OrderByDescending(e => e.OccurredAt)
                .Skip(Math.Max(pageIndex, 0) * Math.Clamp(pageSize, 1, 500))
                .Take(Math.Clamp(pageSize, 1, 500))
                .ToArray();
        }
    }
}
