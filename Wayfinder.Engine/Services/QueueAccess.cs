using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>
/// The access rules for work, as pure predicates over an instance, its blueprint and the caller's
/// <see cref="ActorProfile"/>: who may reach an instance, start a queue, see a row and act on it.
/// </summary>
internal static class QueueAccess
{
    public static bool CanAccessInstance(
        ServiceRequest instance,
        string tenantId,
        string userId,
        ActorProfile accessProfile)
    {
        if (!string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return !accessProfile.RestrictToInstanceOwner
               || string.Equals(instance.UserId, userId, StringComparison.Ordinal);
    }

    public static bool CanStartInitialState(ServiceBlueprint definition, ActorProfile accessProfile)
    {
        var initialStage = definition.Stages.FirstOrDefault(stage =>
            string.Equals(stage.StageKey, definition.InitialStage, StringComparison.Ordinal));

        var queueName = initialStage is null ? null : ResolveQueueName(definition, initialStage);
        var queueKey = initialStage is null ? null : GetQueueKey(initialStage);
        return accessProfile.CanViewQueue(queueName)
            && accessProfile.CanStartQueue(queueName)
            && HasQueueEligibility(definition, queueKey, queueName, accessProfile);
    }

    /// <summary>
    /// Whether <paramref name="accessProfile"/> holds a capability <see cref="QueueDefinition.RoleGates"/>
    /// requires for this queue — a genuine, enforced check, unlike the pre-existing
    /// <c>ServiceBlueprintRouteDefinition.RequiresRole</c> (declared on routes but never actually
    /// validated against the accessing actor; see docs/guides/work-allocation.md). Null/no matching
    /// <see cref="QueueDefinition"/>, or one with no declared <c>RoleGates</c>, is unrestricted.
    /// </summary>
    public static bool HasQueueEligibility(ServiceBlueprint definition, string? queueKey, string? queueName, ActorProfile accessProfile)
    {
        var lookupKey = FirstNonEmpty(queueKey, queueName);
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return true;
        }

        var queue = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, lookupKey, StringComparison.Ordinal));
        return accessProfile.HasCapability(queue?.RoleGates);
    }

    /// <summary>
    /// Resolves who (if anyone) currently owns a queue's row on this instance — for a queue with no
    /// declared <see cref="QueueDefinition.AssignmentPolicy"/>, that's just
    /// <paramref name="cursorAssignedTo"/> (<see cref="RequestCursor.AssignedTo"/>), no team; for a
    /// team-owned queue, it's <see cref="ServiceRequest.QueueAssignments"/> instead —
    /// <see cref="RequestCursor.AssignedTo"/> is never touched for those. See
    /// docs/guides/team-assignment.md.
    /// </summary>
    public static (string? AssignedTo, string? AssignedTeamId) ResolveQueueOwnership(
        QueueDefinition? queueDef, ServiceRequest instance, string? queueKey, string? cursorAssignedTo)
    {
        if (queueDef?.AssignmentPolicy is null)
        {
            return (cursorAssignedTo, null);
        }

        var assignment = instance.QueueAssignments.GetValueOrDefault(queueKey ?? "");
        return (assignment?.AssignedUserId, assignment?.TeamId ?? queueDef.OwningTeamId);
    }

    /// <summary>
    /// Whether a row is visible to <paramref name="userId"/> at all. Held by a specific different
    /// individual → hidden, always (whether or not the queue declares a team). Otherwise not yet
    /// picked up: a queue with no declared team is visible to any eligible actor (pickup is
    /// mandatory to *act*, but every eligible actor still needs to be able to see the row exists in
    /// order to pick it up at all); a team-owned queue's unpicked tray row is visible only to team
    /// members — except when <paramref name="accessProfile"/> is the synthetic
    /// <see cref="ActorProfile.UnrestrictedOwner"/>, meaning this is an internal, system-driven call
    /// recursing with a real <paramref name="userId"/> but no real resolved profile (e.g.
    /// <c>ResolveSupportSystemOutcome</c>'s webhook resolution path) — such a call can never
    /// carry real team membership and must not be blocked by a check it structurally can't satisfy.
    /// See docs/guides/team-assignment.md.
    /// </summary>
    public static bool IsVisibleToActor(QueueDefinition? queueDef, string? assignedTo, ActorProfile accessProfile, string? userId)
    {
        if (userId is null)
        {
            return true;
        }

        if (assignedTo is not null)
        {
            return string.Equals(assignedTo, userId, StringComparison.Ordinal);
        }

        return queueDef?.AssignmentPolicy is null
            || accessProfile.IsTeamMember(queueDef.OwningTeamId)
            || ReferenceEquals(accessProfile, ActorProfile.UnrestrictedOwner);
    }

    /// <summary>
    /// Whether the caller may actually submit an eligible action right now, as opposed to merely
    /// being allowed to see the row exists. The rule is universal, no per-queue opt-out: if a row
    /// isn't assigned to <paramref name="userId"/>, they can't act on it, full stop — whether that
    /// queue declares a <c>QueueDefinition.AssignmentPolicy</c> or not. A queue declaring nothing
    /// still requires an explicit <c>PickupWorkItem</c> first (see docs/guides/work-allocation.md);
    /// it's simply not scoped to any particular team the way <c>"team-tray"</c> is, so any actor
    /// already eligible to see the queue at all may pick it up. The one genuine exemption is
    /// <paramref name="accessProfile"/>.<see cref="ActorProfile.RestrictToInstanceOwner"/> — an
    /// owner-restricted (citizen-style) profile's own instance has exactly one possible actor by
    /// construction, so "assignment" isn't a concept that applies there at all (the same
    /// discriminator <see cref="AccessibleWorkItem.ResolvePickupState"/> already uses for the
    /// identical reason). Internal peeks (no <paramref name="userId"/>) are always entitled too.
    /// </summary>
    public static bool IsEntitledToActNow(ActorProfile accessProfile, string? assignedTo, string? userId) =>
        accessProfile.RestrictToInstanceOwner
        || userId is null
        || string.Equals(assignedTo, userId, StringComparison.Ordinal);

    public static bool CanViewQueue(
        ServiceBlueprint definition,
        string? queueKey,
        string? queueName,
        ActorProfile accessProfile)
    {
        return accessProfile.CanViewQueue(queueName)
            && HasQueueEligibility(definition, queueKey, queueName, accessProfile);
    }
}
