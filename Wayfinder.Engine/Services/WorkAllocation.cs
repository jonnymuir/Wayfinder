using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Extensions;
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Pickup and putback of work items: an actor takes ownership of a row (or a team-tray row) before acting on it, and can hand
/// it back. See docs/guides/work-allocation.md and docs/guides/team-assignment.md.
/// </summary>
internal sealed class WorkAllocation(
    InstanceRepository instances,
    BlueprintRegistry registry,
    WorkItemFinder workItems,
    IEnvelopeSource envelopes)
{
    /// <summary>
    /// Picks up one specific work item for <paramref name="userId"/> — see <see cref="IProcessManager.PickupWorkItem"/>'s
    /// own remarks for the full error-code contract. Reads fresh and retries its own internal CAS
    /// (<see cref="IServiceRequestStore.TrySaveIfVersionMatches"/>) a bounded number of times rather
    /// than asking the caller for an expected version — picking up carries no field edits to lose.
    /// Materializes a real cursor the first time an instance that hasn't yet crossed a gateway
    /// (<c>Cursors.Count == 0</c>) is picked up — see <see cref="RequestCursor.PrimaryCursorId"/>'s
    /// own remarks; this permanently switches the instance onto the multi-cursor bookkeeping path
    /// for the rest of its life, the same as crossing a real gateway would.
    /// </summary>
    public ServiceRequestResponseEnvelope PickupWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!instances.TryGet(instanceId, out var instance))
            {
                return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
            {
                return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
            }

            if (!registry.TryGet(instance.BlueprintKey, out var definition))
            {
                return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
            }

            // Resolved WITHOUT the ownership filter (no userId) — unlike every other caller of
            // FindActorWorkItems, picking up needs to see a cursor that's genuinely eligible but
            // already held by someone else, in order to report ALREADY_PICKED_UP rather than
            // INVALID_TRANSITION. Queue visibility/capability eligibility is still fully enforced
            // (CanViewQueue/HasQueueEligibility never depend on userId), so this can't be used to
            // discover anything about a queue this actor genuinely isn't eligible for.
            var item = workItems.FindActorWorkItems(instance, definition, accessProfile)
                .FirstOrDefault(candidate => string.Equals(candidate.CursorId, cursorId, StringComparison.Ordinal));
            if (item is null)
            {
                return Envelopes.Error($"Cursor '{cursorId}' is not accessible on this instance.", "INVALID_TRANSITION");
            }

            if (accessProfile.RestrictToInstanceOwner)
            {
                return Envelopes.Error("This item is not available to pick up.", "PICKUP_NOT_AVAILABLE");
            }

            ServiceRequest updatedInstance;
            if (item.AssignmentPolicy == AssignmentPolicies.AssignToInitiator)
            {
                // Always already owned by whoever started it — see docs/guides/team-assignment.md's
                // reassignment scope note.
                return Envelopes.Error(
                    "This item is always assigned to whoever started it — there's nothing to pick up.", "PICKUP_NOT_AVAILABLE");
            }

            if (item.AssignmentPolicy == AssignmentPolicies.TeamTray)
            {
                if (!accessProfile.IsTeamMember(item.AssignedTeamId))
                {
                    return Envelopes.Error("You must be a member of the owning team to pick up this item.", "TEAM_MEMBERSHIP_REQUIRED");
                }

                if (item.AssignedTo is not null && !string.Equals(item.AssignedTo, userId, StringComparison.Ordinal))
                {
                    return Envelopes.Error("This item has already been picked up by someone else.", "ALREADY_PICKED_UP");
                }

                // Not item.Classify(definition) != Unassigned here: item was resolved via
                // the userId-less internal peek above, so IsEntitledToActNow's "no specific actor"
                // shortcut always reports entitled, which would make ClassifyStatus report
                // Actionable even for a genuinely not-picked-up row — EligibleActions (assignment-
                // agnostic) is the right check for "is there really something to pick up here at all".
                if (item.EligibleActions.Count == 0)
                {
                    return Envelopes.Error("This item is not available to pick up.", "PICKUP_NOT_AVAILABLE");
                }

                var teamPickedUpAt = DateTimeOffset.UtcNow;
                var queueKey = item.QueueKey ?? "";
                var existingAssignment = instance.QueueAssignments.GetValueOrDefault(queueKey);
                var assignment = (existingAssignment
                        ?? new QueueAssignment { QueueKey = queueKey, TeamId = item.AssignedTeamId, EstablishedAt = teamPickedUpAt })
                    with
                { AssignedUserId = userId, AssignedAt = teamPickedUpAt };

                updatedInstance = instance with
                {
                    QueueAssignments = new Dictionary<string, QueueAssignment>(instance.QueueAssignments) { [queueKey] = assignment },
                    StateVersion = instance.StateVersion + 1,
                    UpdatedAt = teamPickedUpAt
                };
            }
            else
            {
                // No AssignmentPolicy declared — pickup is still mandatory (see
                // docs/guides/work-allocation.md), just not scoped to any particular team: any
                // actor already eligible to see this queue at all may pick it up. Same
                // EligibleActions check as the team-tray branch above, for the identical reason —
                // ClassifyStatus would report Actionable here even for a genuinely not-picked-up
                // row, since this call resolved the item via the userId-less internal peek.
                if (item.AssignedTo is not null && !string.Equals(item.AssignedTo, userId, StringComparison.Ordinal))
                {
                    return Envelopes.Error("This item has already been picked up by someone else.", "ALREADY_PICKED_UP");
                }

                if (item.EligibleActions.Count == 0)
                {
                    return Envelopes.Error("This item is not available to pick up.", "PICKUP_NOT_AVAILABLE");
                }

                var pickedUpAt = DateTimeOffset.UtcNow;
                updatedInstance = instance.Cursors.Count == 0
                    ? instance with
                    {
                        Cursors =
                        [
                            new RequestCursor
                            {
                                CursorId = RequestCursor.PrimaryCursorId,
                                QueueKey = GetQueueKey(definition.Stages.FirstOrDefault(s =>
                                    string.Equals(s.StageKey, instance.CurrentStage, StringComparison.Ordinal))) ?? "",
                                CurrentNodeKey = instance.CurrentStage,
                                IsAtGateway = false,
                                AssignedTo = userId,
                                AssignedAt = pickedUpAt
                            }
                        ],
                        StateVersion = instance.StateVersion + 1,
                        UpdatedAt = pickedUpAt
                    }
                    : instance with
                    {
                        Cursors = instance.Cursors
                            .Select(c => string.Equals(c.CursorId, cursorId, StringComparison.Ordinal)
                                ? c with { AssignedTo = userId, AssignedAt = pickedUpAt }
                                : c)
                            .ToArray(),
                        StateVersion = instance.StateVersion + 1,
                        UpdatedAt = pickedUpAt
                    };
            }

            var savedPickup = instances.TrySaveIfVersionMatches(updatedInstance, userId, instance.StateVersion,
                    AuditEvents.WorkItem(instance.InstanceId, userId, cursorId, AuditEventType.PickedUp, "picked up"));
            if (savedPickup is not null)
            {
                return envelopes.BuildEnvelope(savedPickup, definition, accessProfile, userId);
            }
        }

        return Envelopes.Error(
            $"Could not pick up '{cursorId}' after {maxAttempts} attempts due to concurrent updates.",
            "PICKUP_CONFLICT");
    }

    /// <summary>
    /// Puts back a pickup <paramref name="userId"/> currently holds, to the shared pool — see
    /// <see cref="IProcessManager.PutbackWorkItem"/>'s own remarks. Self-service only: someone
    /// else's pickup can't be put back this way (see docs/guides/work-allocation.md's reassignment
    /// seam for the future manager-initiated case).
    /// </summary>
    public ServiceRequestResponseEnvelope PutbackWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!instances.TryGet(instanceId, out var instance))
            {
                return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
            {
                return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
            }

            if (!registry.TryGet(instance.BlueprintKey, out var definition))
            {
                return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
            }

            var cursor = instance.Cursors.FirstOrDefault(c => string.Equals(c.CursorId, cursorId, StringComparison.Ordinal));
            if (cursor is null)
            {
                return Envelopes.Error($"Cursor '{cursorId}' not found.", "INVALID_TRANSITION");
            }

            var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, cursor.QueueKey, StringComparison.Ordinal));

            ServiceRequest updatedInstance;
            if (queueDef?.AssignmentPolicy == AssignmentPolicies.TeamTray)
            {
                var queueKey = cursor.QueueKey;
                var existingAssignment = instance.QueueAssignments.GetValueOrDefault(queueKey);

                if (existingAssignment?.AssignedUserId is null)
                {
                    return envelopes.BuildEnvelope(instance, definition, accessProfile, userId);
                }

                if (!string.Equals(existingAssignment.AssignedUserId, userId, StringComparison.Ordinal))
                {
                    return Envelopes.Error("This item has been picked up by someone else.", "ALREADY_PICKED_UP_BY_OTHER");
                }

                // Back to the team tray — still team-owned, just not by a specific individual.
                var putBack = existingAssignment with { AssignedUserId = null, AssignedAt = null };
                updatedInstance = instance with
                {
                    QueueAssignments = new Dictionary<string, QueueAssignment>(instance.QueueAssignments) { [queueKey] = putBack },
                    StateVersion = instance.StateVersion + 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            else if (queueDef?.AssignmentPolicy == AssignmentPolicies.AssignToInitiator)
            {
                return Envelopes.Error(
                    "This item is always assigned to whoever started it and can't be put back.", "PICKUP_NOT_AVAILABLE");
            }
            else
            {
                // No AssignmentPolicy declared — ownership tracked on RequestCursor.AssignedTo
                // directly rather than ServiceRequest.QueueAssignments (see ResolveQueueOwnership).
                if (cursor.AssignedTo is null)
                {
                    return envelopes.BuildEnvelope(instance, definition, accessProfile, userId);
                }

                if (!string.Equals(cursor.AssignedTo, userId, StringComparison.Ordinal))
                {
                    return Envelopes.Error("This item has been picked up by someone else.", "ALREADY_PICKED_UP_BY_OTHER");
                }

                updatedInstance = instance with
                {
                    Cursors = instance.Cursors
                        .Select(c => string.Equals(c.CursorId, cursorId, StringComparison.Ordinal)
                            ? c with { AssignedTo = null, AssignedAt = null }
                            : c)
                        .ToArray(),
                    StateVersion = instance.StateVersion + 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }

            var savedPutback = instances.TrySaveIfVersionMatches(updatedInstance, userId, instance.StateVersion,
                    AuditEvents.WorkItem(instance.InstanceId, userId, cursorId, AuditEventType.PutBack, "put back in the pool"));
            if (savedPutback is not null)
            {
                return envelopes.BuildEnvelope(savedPutback, definition, accessProfile, userId);
            }
        }

        return Envelopes.Error(
            $"Could not put back '{cursorId}' after {maxAttempts} attempts due to concurrent updates.",
            "PICKUP_CONFLICT");
    }

    /// <summary>
    /// See <see cref="IProcessManager.PickupNextAvailableWorkItem"/>. Scans for the oldest eligible,
    /// not-picked-up, Actionable row (by <see cref="ServiceRequest.CreatedAt"/>, tiebroken by
    /// <see cref="ServiceRequest.InstanceId"/> for determinism — the same ordering
    /// <c>GetQueueWorkItems</c>'s own sort uses) and attempts <see cref="PickupWorkItem"/> on it;
    /// if that loses the race to a concurrent caller, moves on to the next candidate rather than
    /// giving up — <see cref="PickupWorkItem"/>'s own internal CAS retry only covers contention on
    /// the *same* row, not two different automated callers converging on different rows that both
    /// turn out already spoken for by the time each is attempted.
    /// </summary>
    public QueueWorkItem? PickupNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile)
    {
        var candidates = instances.GetAll()
            .Where(instance => string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal))
            .SelectMany(instance =>
            {
                if (!registry.TryGet(instance.BlueprintKey, out var definition))
                {
                    return Array.Empty<(ServiceRequest Instance, ServiceBlueprint Definition, ActorWorkItem Item)>();
                }

                if (accessProfile.RestrictToInstanceOwner)
                {
                    return Array.Empty<(ServiceRequest Instance, ServiceBlueprint Definition, ActorWorkItem Item)>();
                }

                // EligibleActions.Count > 0, not ClassifyStatus — this resolves items via the
                // userId-less internal peek (no userId passed to FindActorWorkItems below), so
                // IsEntitledToActNow's own "no specific actor" shortcut always reports entitled,
                // which would make ClassifyStatus report Actionable even for a genuinely
                // not-picked-up row (the same reason PickupWorkItem's own two branches check
                // EligibleActions rather than ClassifyStatus). Found live as a pre-existing gap:
                // the team-tray clause here never correctly matched an unpicked row before this
                // fix, since ClassifyStatus == Unassigned could never be true at this call site.
                return workItems.FindActorWorkItems(instance, definition, accessProfile)
                    .Where(item => item.AssignedTo is null
                        && item.EligibleActions.Count > 0
                        && (item.AssignmentPolicy is null
                            || item.AssignmentPolicy == AssignmentPolicies.TeamTray && accessProfile.IsTeamMember(item.AssignedTeamId)))
                    .Select(item => (Instance: instance, Definition: definition, Item: item))
                    .ToArray();
            })
            .OrderBy(candidate => candidate.Instance.CreatedAt)
            .ThenBy(candidate => candidate.Instance.InstanceId, StringComparer.Ordinal)
            .ToArray();

        foreach (var (instance, definition, item) in candidates)
        {
            var pickedUp = PickupWorkItem(instance.InstanceId, item.CursorId, tenantId, userId, accessProfile);
            if (pickedUp.ResponseState != "error" && instances.TryGet(instance.InstanceId, out var refreshedInstance))
            {
                // Resolved from refreshedInstance, not the pre-pickup `instance` this loop iterates
                // over — AssignedTo only reflects the pickup just performed once read fresh.
                var refreshedItem = workItems.FindActorWorkItems(refreshedInstance, definition, accessProfile, userId)
                    .FirstOrDefault(candidate => string.Equals(candidate.CursorId, item.CursorId, StringComparison.Ordinal));
                if (refreshedItem is not null)
                {
                    return refreshedItem.ToEnvelopeItem(
                        refreshedInstance, definition, QueueWorkItemStatus.Actionable, accessProfile, userId);
                }
            }
            // Someone else picked up this exact row between the scan and this attempt — try the
            // next-oldest candidate rather than giving up.
        }

        return null;
    }
}
