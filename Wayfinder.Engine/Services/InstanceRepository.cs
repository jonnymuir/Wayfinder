using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Every read and write of a stored <see cref="ServiceRequest"/> goes through here, so the one rule
/// that must hold on every write — a queue's assignment is established the first time work lands in
/// it — cannot be skipped by a new caller, and every audited write is recorded the same way.
/// </summary>
internal sealed class InstanceRepository(IServiceRequestStore store, IAuditLogStore auditLog, BlueprintRegistry registry)
{
    private readonly BlueprintRegistry _registry = registry;

    public bool TryGet(string instanceId, out ServiceRequest instance) => store.TryGet(instanceId, out instance!);

    public IEnumerable<ServiceRequest> GetAll() => store.GetAll();

    public bool Remove(string instanceId) => store.Remove(instanceId);

    public void Clear() => store.Clear();

    /// <summary>Persists an instance exactly as given, with no queue-assignment step (an administrator's abort).</summary>
    public void SaveAsIs(ServiceRequest instance) => store.Save(instance);

    /// <summary>
    /// Returns the actually-persisted instance (post <see cref="EstablishQueueAssignmentsIfNeeded"/>),
    /// not the pre-save argument — a caller building a render envelope from the return value (not
    /// the stale local it passed in) sees a queue assignment established by this very save, e.g. an
    /// <c>assign-to-initiator</c> queue's very first stage rendering its action buttons on the same
    /// response that created the instance, rather than only from the *next* request. Confirmed live:
    /// building the envelope from the pre-save argument instead left a brand-new instance on such a
    /// queue with zero available actions until a second page load re-read the store fresh.
    /// </summary>
    public ServiceRequest Save(ServiceRequest instance, string actingUserId, AuditEvent? auditEvent = null)
    {
        var established = EstablishQueueAssignmentsIfNeeded(instance, actingUserId);
        store.Save(established);
        if (auditEvent is not null)
        {
            auditLog.Record(auditEvent);
        }

        return established;
    }

    /// <summary>
    /// The audited counterpart to <see cref="IServiceRequestStore.TrySaveIfVersionMatches"/> — used
    /// by the plain (non-gateway) mutation paths of <c>Advance</c>, where two callers racing against the
    /// same not-yet-picked-up item is a real, user-facing concern (see docs/guides/work-allocation.md).
    /// Records <paramref name="auditEvent"/> only when the save actually lands. Returns the established,
    /// actually-persisted instance on success (see <see cref="Save"/>'s own remarks on why a caller
    /// must render from this, not its pre-save argument) — <see langword="null"/> on a CAS conflict.
    /// </summary>
    public ServiceRequest? TrySaveIfVersionMatches(ServiceRequest instance, string actingUserId, int expectedStateVersion, AuditEvent? auditEvent)
    {
        var established = EstablishQueueAssignmentsIfNeeded(instance, actingUserId);
        if (!store.TrySaveIfVersionMatches(established, expectedStateVersion))
        {
            return null;
        }

        if (auditEvent is not null)
        {
            auditLog.Record(auditEvent);
        }

        return established;
    }

    /// <summary>
    /// Establishes a durable <see cref="QueueAssignment"/> the first time any of this instance's
    /// current cursors (or, pre-first-gateway, its own <see cref="ServiceRequest.CurrentStage"/>)
    /// lands in a team-owned queue (<see cref="QueueDefinition.AssignmentPolicy"/> declared) it has
    /// no existing record for — add-if-absent, never overwritten, so a later re-entry into the same
    /// queue key reuses the same record rather than re-running that queue's policy. This is the one
    /// hook every <see cref="Save"/>/<see cref="TrySaveIfVersionMatches"/> call goes
    /// through — deliberately not threaded into each individual cursor-minting call site (Split
    /// fan-out, Join arrival/release) so a future new mint site can't silently skip establishment.
    /// A queue without an assignment policy (no <see cref="QueueDefinition.AssignmentPolicy"/>) is never touched here —
    /// see <see cref="RequestCursor.AssignedTo"/> for that case. See docs/guides/team-assignment.md.
    /// </summary>
    private ServiceRequest EstablishQueueAssignmentsIfNeeded(ServiceRequest instance, string actingUserId)
    {
        if (!_registry.TryGet(instance.BlueprintKey, out var definition))
        {
            return instance;
        }

        Dictionary<string, QueueAssignment>? additions = null;

        void TryAddAssignment(string? queueKey)
        {
            if (string.IsNullOrWhiteSpace(queueKey)
                || instance.QueueAssignments.ContainsKey(queueKey)
                || additions?.ContainsKey(queueKey) == true)
            {
                return;
            }

            var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, queueKey, StringComparison.Ordinal));
            if (queueDef?.AssignmentPolicy is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            additions ??= new Dictionary<string, QueueAssignment>();
            additions[queueKey] = queueDef.AssignmentPolicy == AssignmentPolicies.AssignToInitiator
                ? new QueueAssignment
                {
                    QueueKey = queueKey,
                    TeamId = queueDef.OwningTeamId,
                    AssignedUserId = actingUserId,
                    AssignedAt = now,
                    EstablishedAt = now
                }
                : new QueueAssignment { QueueKey = queueKey, TeamId = queueDef.OwningTeamId, EstablishedAt = now };
        }

        if (instance.Cursors.Count == 0)
        {
            var stage = definition.Stages.FirstOrDefault(s => string.Equals(s.StageKey, instance.CurrentStage, StringComparison.Ordinal));
            TryAddAssignment(GetQueueKey(stage));
        }
        else
        {
            foreach (var cursor in instance.Cursors.Where(c => !c.IsAtGateway))
            {
                TryAddAssignment(cursor.QueueKey);
            }
        }

        if (additions is null)
        {
            return instance;
        }

        var merged = new Dictionary<string, QueueAssignment>(instance.QueueAssignments);
        foreach (var (key, value) in additions)
        {
            merged[key] = value;
        }

        return instance with { QueueAssignments = merged };
    }
}
