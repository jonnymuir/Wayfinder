using Wayfinder.Engine.Models;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>
/// <paramref name="EligibleActions"/> is route/role/showWhen-gated only — assignment-agnostic,
/// feeds <see cref="ClassifyStatus"/>. <paramref name="AvailableActions"/> narrows that to
/// nothing unless the caller is individually entitled to act on this row *right now* — feeds
/// rendering (<see cref="BuildEnvelope"/>'s <c>StepContent.AvailableActions</c>) and what
/// <see cref="Advance(string,string,string,ActorProfile,string,int,Dictionary{string,object?}?)"/>
/// will accept. Splitting these two is what lets an unassigned team-tray row stay visible and
/// available to pick up (eligible) while still rendering zero action buttons (not yet available) — see
/// docs/guides/team-assignment.md. <paramref name="AssignmentPolicy"/>/<paramref name="AssignedTeamId"/>
/// are null for a queue without an assignment policy.
/// </summary>
internal sealed record AccessibleWorkItem(
    string StageKey,
    string DisplayName,
    string? QueueName,
    string? QueueKey,
    bool IsJoinGateway,
    IReadOnlyList<ServiceRequestAction> EligibleActions,
    IReadOnlyList<ServiceRequestAction> AvailableActions,
    string? AssignmentPolicy,
    string CursorId,
    string? AssignedTo,
    string? AssignedTeamId)
{
    public QueueWorkItem ToEnvelopeItem(
        ServiceRequest instance,
        ServiceBlueprint definition,
        QueueWorkItemStatus status,
        ActorProfile accessProfile,
        string userId) =>
        new()
        {
            InstanceId = instance.InstanceId,
            BlueprintKey = instance.BlueprintKey,
            BlueprintDisplayName = definition.DisplayName,
            StageKey = StageKey,
            StateDisplayName = DisplayName,
            QueueName = QueueName,
            CursorId = CursorId,
            TenantId = instance.TenantId,
            UserId = instance.UserId,
            StateVersion = instance.StateVersion,
            AvailableActions = AvailableActions,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = instance.UpdatedAt,
            Status = status,
            PickupState = ResolvePickupState(status, accessProfile)
        };

    /// <summary>
    /// See docs/guides/work-allocation.md and docs/guides/team-assignment.md. A queue with no
    /// declared <c>AssignmentPolicy</c> behaves identically to <c>"team-tray"</c> here — pickup
    /// is mandatory either way, the only difference is team-tray additionally scopes who may
    /// pick a row up to a specific team's own members. Unassigned status is itself the "not
    /// picked up, pick me up" affordance; Actionable means already picked up by this caller. An
    /// assign-to-initiator queue always has nothing to pick up/put back — it's already owned
    /// the moment it exists (see docs/guides/team-assignment.md's reassignment scope note).
    /// </summary>
    private QueueWorkItemPickupState? ResolvePickupState(QueueWorkItemStatus status, ActorProfile accessProfile)
    {
        if (accessProfile.RestrictToInstanceOwner)
        {
            return null;
        }

        return AssignmentPolicy switch
        {
            null or AssignmentPolicies.TeamTray => status switch
            {
                QueueWorkItemStatus.Unassigned => QueueWorkItemPickupState.NotPickedUp,
                QueueWorkItemStatus.Actionable => QueueWorkItemPickupState.PickedUpByMe,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// Which bucket a work item falls into — <see langword="null"/> for a row that is none of the
    /// four: no eligible routes at all (the actor may simply lack permission to act in that queue,
    /// or every outgoing route may be <c>showWhen</c>-hidden), not waiting at a join gateway, and
    /// not genuinely terminal either. Those rows stay invisible under every status filter,
    /// including all four selected at once — the same as they always have been. Eligible routes
    /// exist but aren't individually available right now → <see cref="QueueWorkItemStatus.Unassigned"/>
    /// (a team-tray row nobody has picked up — see docs/guides/team-assignment.md); eligible AND
    /// available → <see cref="QueueWorkItemStatus.Actionable"/>.
    /// </summary>
    public QueueWorkItemStatus? Classify(ServiceBlueprint definition)
    {
        if (EligibleActions.Count > 0)
        {
            return AvailableActions.Count > 0 ? QueueWorkItemStatus.Actionable : QueueWorkItemStatus.Unassigned;
        }

        if (IsJoinGateway)
        {
            return QueueWorkItemStatus.Waiting;
        }

        return IsTerminal(definition) ? QueueWorkItemStatus.Done : null;
    }

    /// <summary>
    /// The per-item predicate <c>IsTerminalInstance</c> is built from — extracted so
    /// <c>GetQueueWorkItems</c> can classify every row of a multi-cursor instance
    /// individually, rather than the first-accessible-item-only view <c>IsTerminalInstance</c>
    /// needs for its own "is *the* instance terminal" question.
    /// </summary>
    public bool IsTerminal(ServiceBlueprint definition)
    {
        if (IsJoinGateway)
        {
            return false;
        }

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == StageKey);
        return stage != null && stage.Components.InferStepType() == "confirmation";
    }
}
