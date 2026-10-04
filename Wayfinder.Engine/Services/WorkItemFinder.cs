using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>Works out which rows of an instance (stages it is at, join gateways it waits behind) a given actor can see, and what they can do.</summary>
internal sealed class WorkItemFinder(StageCalculations calculations)
{
    public IReadOnlyList<AccessibleWorkItem> FindAccessibleWorkItems(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string? userId = null)
    {
        var items = new List<AccessibleWorkItem>();

        if (instance.Cursors.Count == 0)
        {
            var stage = definition.Stages.FirstOrDefault(candidate =>
                string.Equals(candidate.StageKey, instance.CurrentStage, StringComparison.Ordinal));

            if (stage is not null)
            {
                var queueKey = GetQueueKey(stage);
                var queueName = ResolveQueueName(definition, stage);
                if (CanViewQueue(definition, queueKey, queueName, accessProfile))
                {
                    var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, queueKey, StringComparison.Ordinal));
                    var (assignedTo, assignedTeamId) = ResolveQueueOwnership(queueDef, instance, queueKey, cursorAssignedTo: null);

                    if (IsVisibleToActor(queueDef, assignedTo, accessProfile, userId))
                    {
                        var eligibleActions = BuildEligibleActions(instance, definition, stage.StageKey, queueName, accessProfile);
                        var availableActions = IsEntitledToActNow(accessProfile, assignedTo, userId) ? eligibleActions : [];

                        items.Add(new AccessibleWorkItem(
                            stage.StageKey,
                            stage.DisplayName,
                            queueName,
                            queueKey,
                            IsJoinGateway: false,
                            eligibleActions,
                            availableActions,
                            queueDef?.AssignmentPolicy,
                            CursorId: RequestCursor.PrimaryCursorId,
                            AssignedTo: assignedTo,
                            AssignedTeamId: assignedTeamId));
                    }
                }
            }

            return items;
        }

        foreach (var cursor in instance.Cursors.Where(candidate => !candidate.IsAtGateway))
        {
            var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, cursor.QueueKey, StringComparison.Ordinal));
            var (assignedTo, assignedTeamId) = ResolveQueueOwnership(queueDef, instance, cursor.QueueKey, cursor.AssignedTo);

            // A row held by someone else — a specific individual on a queue without an assignment policy, or a different
            // team member on a team-owned one — is hidden entirely, not shown-but-disabled — the
            // same enforcement Advance's own target resolution relies on (see
            // docs/guides/work-allocation.md and docs/guides/team-assignment.md). No userId (an
            // internal check that never resolves a specific actor, e.g. RefreshIfWaitingAtJoin
            // peeking at the primary item) skips this filter rather than hiding everything.
            if (!IsVisibleToActor(queueDef, assignedTo, accessProfile, userId))
            {
                continue;
            }

            var stage = definition.Stages.FirstOrDefault(candidate =>
                string.Equals(candidate.StageKey, cursor.CurrentNodeKey, StringComparison.Ordinal));

            if (stage is null)
            {
                continue;
            }

            var queueName = ResolveQueueName(definition, cursor.QueueKey);
            if (!CanViewQueue(definition, cursor.QueueKey, queueName, accessProfile))
            {
                continue;
            }

            var eligibleActions = BuildEligibleActions(instance, definition, stage.StageKey, queueName, accessProfile);
            var availableActions = IsEntitledToActNow(accessProfile, assignedTo, userId) ? eligibleActions : [];

            items.Add(new AccessibleWorkItem(
                stage.StageKey,
                stage.DisplayName,
                queueName,
                cursor.QueueKey,
                IsJoinGateway: false,
                eligibleActions,
                availableActions,
                queueDef?.AssignmentPolicy,
                CursorId: cursor.CursorId,
                AssignedTo: assignedTo,
                AssignedTeamId: assignedTeamId));
        }

        foreach (var cursor in instance.Cursors.Where(candidate => candidate.IsAtGateway))
        {
            var gateway = FindGateway(definition, cursor.CurrentNodeKey);
            if (gateway is null || gateway.GatewayType != GatewayKind.Join)
            {
                continue;
            }

            var queueName = ResolveQueueName(definition, gateway);
            if (!CanViewQueue(definition, gateway.QueueKey, queueName, accessProfile))
            {
                continue;
            }

            items.Add(new AccessibleWorkItem(
                gateway.Key,
                gateway.DisplayName,
                queueName,
                gateway.QueueKey,
                IsJoinGateway: true,
                [],
                [],
                AssignmentPolicy: null,
                CursorId: cursor.CursorId,
                AssignedTo: null,
                AssignedTeamId: null));
        }

        return items
            .OrderByDescending(item => string.Equals(item.StageKey, instance.CurrentStage, StringComparison.Ordinal))
            .ThenBy(item => item.IsJoinGateway)
            .ThenBy(item => item.StageKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Assignment-agnostic — route/role/showWhen-gated only. See
    /// <see cref="AccessibleWorkItem.EligibleActions"/>'s own remarks for why this is a distinct
    /// concept from what actually renders/what <c>Advance</c> accepts.
    /// </summary>
    public ServiceRequestAction[] BuildEligibleActions(
        ServiceRequest instance,
        ServiceBlueprint definition,
        string stageKey,
        string? queueName,
        ActorProfile accessProfile)
    {
        var transitions = GetOutgoingTransitions(definition, stageKey);

        if (!string.IsNullOrWhiteSpace(queueName) && !accessProfile.CanActInQueue(queueName))
        {
            return [];
        }

        // ServiceBlueprintRouteDefinition.RequiresRole — genuinely enforced against
        // ActorProfile.Capabilities, unlike the check this replaced (which only ever excluded a
        // role-gated route when there was no queue context at all, and never validated the actor's
        // specific role even then). Reuses Capabilities rather than a separate "Roles" set, since
        // both already express "does this actor hold X" — see docs/guides/work-allocation.md for
        // why this is a different, pre-existing concept from RequiredCapabilities/RoleGates'
        // queue-eligibility gate above it.
        transitions = transitions
            .Where(transition => string.IsNullOrWhiteSpace(transition.RequiresRole)
                || accessProfile.Capabilities.Contains(transition.RequiresRole))
            .ToArray();

        // ServiceBlueprintRouteDefinition.ShowWhen excludes a route from AvailableActions
        // entirely (not merely disables it) — the same mechanism a stage's own components use via
        // Component.ShowWhen. The Any() guard is deliberate: this runs once per stage per queue
        // render (FindAccessibleWorkItems calls it for every visible cursor across every instance
        // a queue lists), so a blueprint that never uses ShowWhen on a route — everything shipped
        // before this — pays nothing extra for it.
        if (transitions.Any(transition => !string.IsNullOrWhiteSpace(transition.ShowWhen)))
        {
            var stage = definition.Stages.FirstOrDefault(candidate =>
                string.Equals(candidate.StageKey, stageKey, StringComparison.Ordinal));
            if (stage is not null)
            {
                var scope = calculations.BuildCalculationScope(instance, definition, stage, pendingFieldValues: null);
                transitions = transitions
                    .Where(transition => calculations.EvaluateShowWhen(transition.ShowWhen, scope, definition.Calculations))
                    .ToArray();
            }
        }

        return transitions
            .Select(transition => new ServiceRequestAction
            {
                ActionKey = transition.Action,
                // `??` alone doesn't catch an empty-but-non-null Label, which is exactly what an
                // agent leaving the field blank (rather than omitting it) produces — treat blank
                // the same as absent. transition.Action is never blank by the time it gets here;
                // GetOutgoingTransitions defaults it below, the one place raw route.Trigger values
                // are read, so every consumer (this, and the action-matching in Advance) agrees.
                Label = string.IsNullOrWhiteSpace(transition.Label) ? ActionLabel(transition.Action) : transition.Label,
                Style = transition.Style ?? ActionStyle(transition.Action)
            })
            .ToArray();
    }

    private static string ActionLabel(string key) => key switch
    {
        "submit" => "Submit",
        "save-draft" => "Save Draft",
        "start-another" => "Start Another",
        "approve" => "Approve",
        "request-changes" => "Request Changes",
        "continue" => "Continue",
        _ => key
    };

    private static string ActionStyle(string key) => key switch
    {
        "submit" or "approve" => "primary",
        "reject" or "cancel" => "destructive",
        _ => "secondary"
    };
}
