using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>
/// The caseworker's queue views: the work items an actor can see in a queue or a team's tray, filtered, searched
/// and sorted. <paramref name="tryPollSupportSystems"/> gives a row waiting at a join a chance to resolve before it is listed.
/// </summary>
internal sealed class WorkQueues(
    InstanceRepository instances,
    BlueprintRegistry registry,
    WorkItemFinder workItems,
    Func<ServiceRequest, ServiceBlueprint, ServiceBlueprintGatewayDefinition, bool> tryPollSupportSystems)
{
    private static readonly IReadOnlyCollection<QueueWorkItemStatus> DefaultQueueWorkItemStatuses =
        [QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting, QueueWorkItemStatus.Unassigned];

    private static QueueWorkItem[] ApplyQueueWorkListSort(
        IReadOnlyList<QueueWorkItem> items, QueueWorkListSort sort) => sort switch
    {
        QueueWorkListSort.CreatedAtNewestFirst => items
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        QueueWorkListSort.CreatedAtOldestFirst => items
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        QueueWorkListSort.UpdatedAtNewestFirst => items
            .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        QueueWorkListSort.UpdatedAtOldestFirst => items
            .OrderBy(i => i.UpdatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        _ => items
            .OrderBy(i => i.BlueprintDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.StateDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.InstanceId, StringComparer.Ordinal)
            .ToArray(),
    };

    /// <summary>
    /// Free-text match against the projected display fields plus every raw
    /// <see cref="ServiceRequest.FieldValues"/> value — not added to <see cref="QueueWorkItem"/>
    /// itself, since <c>FieldValues</c> is arbitrary, blueprint-defined, and never otherwise
    /// rendered in the list. A <see cref="System.Text.Json.JsonElement"/> value (a real shape here,
    /// not merely defensive) is stringified via its own JSON text rather than
    /// <see cref="object.ToString"/>, which would just yield its CLR type name.
    /// </summary>
    private static bool MatchesQueueSearch(ServiceRequest instance, QueueWorkItem projected, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        bool Contains(string? haystack) =>
            !string.IsNullOrEmpty(haystack) && haystack.Contains(searchText, StringComparison.OrdinalIgnoreCase);

        if (Contains(projected.InstanceId) || Contains(projected.BlueprintDisplayName) || Contains(projected.StateDisplayName))
        {
            return true;
        }

        foreach (var value in instance.FieldValues.Values)
        {
            if (value is null)
            {
                continue;
            }

            var text = value is System.Text.Json.JsonElement jsonElement ? jsonElement.ToString() : value.ToString();
            if (Contains(text))
            {
                return true;
            }
        }

        return false;
    }

    public QueueWorkListEnvelope GetQueueWorkItems(
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20)
    {
        // null (the C# default) means "apply the engine's own default view" — an explicitly empty,
        // non-null collection means "show nothing", respected literally. A host route can't rely
        // on C# defaults over an HTTP call, so it must preserve this distinction itself (see
        // docs/guides/queue-worklist-filtering.md).
        var effectiveStatuses = statuses ?? DefaultQueueWorkItemStatuses;
        var effectivePageIndex = Math.Max(pageIndex, 0);
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);

        var matched = instances.GetAll()
            .Where(instance => string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal))
            .SelectMany(instance =>
            {
                if (!registry.TryGet(instance.BlueprintKey, out var definition))
                {
                    return Array.Empty<QueueWorkItem>();
                }

                // A deliberate refresh of the list should show reality, not what was true the
                // last time anyone happened to open this specific item — give a waiting item the
                // same poll-resolve chance BuildEnvelope already gives it on a single-instance
                // read, just applied per row here instead of per page. Unconditional, regardless
                // of whether "Waiting" is even in the requested status set: an item that has just
                // resolved must be reclassified off its stale pre-refresh status this request. No
                // background timer: this only ever runs inside a caller's own GET, same as
                // everywhere else this hook is used.
                instance = RefreshIfWaitingAtJoin(instance, definition, accessProfile);

                return workItems.FindAccessibleWorkItems(instance, definition, accessProfile, userId)
                    .Select(item => (item, status: item.Classify(definition)))
                    .Where(pair => pair.status is not null && effectiveStatuses.Contains(pair.status.Value))
                    .Select(pair => pair.item.ToEnvelopeItem(instance, definition, pair.status!.Value, accessProfile, userId))
                    .Where(projected => MatchesQueueSearch(instance, projected, searchText))
                    .ToArray();
            })
            .ToArray();

        var ordered = ApplyQueueWorkListSort(matched, sort);
        var page = ordered.Skip(effectivePageIndex * effectivePageSize).Take(effectivePageSize).ToArray();

        return new QueueWorkListEnvelope
        {
            Items = page,
            PageIndex = effectivePageIndex,
            PageSize = effectivePageSize,
            TotalMatchingCount = ordered.Length
        };
    }

    /// <inheritdoc cref="IProcessManager.GetTeamWorkItems"/>
    public QueueWorkListEnvelope GetTeamWorkItems(
        string tenantId,
        string teamId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20)
    {
        var effectiveStatuses = statuses ?? DefaultQueueWorkItemStatuses;
        var effectivePageIndex = Math.Max(pageIndex, 0);
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);

        if (!accessProfile.IsTeamMember(teamId))
        {
            return new QueueWorkListEnvelope { PageIndex = effectivePageIndex, PageSize = effectivePageSize };
        }

        var matched = instances.GetAll()
            .Where(instance => string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal))
            .SelectMany(instance =>
            {
                if (!registry.TryGet(instance.BlueprintKey, out var definition))
                {
                    return Array.Empty<QueueWorkItem>();
                }

                instance = RefreshIfWaitingAtJoin(instance, definition, accessProfile);

                return FindTeamWorkItems(instance, definition, accessProfile, teamId)
                    .Select(item => (item, status: item.Classify(definition)))
                    .Where(pair => pair.status is not null && effectiveStatuses.Contains(pair.status.Value))
                    .Select(pair => pair.item.ToEnvelopeItem(instance, definition, pair.status!.Value, accessProfile, teamId))
                    .Where(projected => MatchesQueueSearch(instance, projected, searchText))
                    .ToArray();
            })
            .ToArray();

        var orderedTeamItems = ApplyQueueWorkListSort(matched, sort);
        var teamPage = orderedTeamItems.Skip(effectivePageIndex * effectivePageSize).Take(effectivePageSize).ToArray();

        return new QueueWorkListEnvelope
        {
            Items = teamPage,
            PageIndex = effectivePageIndex,
            PageSize = effectivePageSize,
            TotalMatchingCount = orderedTeamItems.Length
        };
    }

    /// <summary>
    /// Team-scoped counterpart to <see cref="FindAccessibleWorkItems"/> — every row whose queue is
    /// owned by <paramref name="teamId"/>, regardless of which individual (if any) currently holds
    /// it, for a team's own aggregate dashboard rather than one caller's personal actionability.
    /// <c>AvailableActions</c> reflects whether *anyone* has picked the row up (not whether the
    /// specific caller has) — "Actionable" here means "a teammate is already on this", not "I can
    /// act on this". See docs/guides/team-assignment.md. Never returns a row from a queue without an assignment policy (no
    /// <c>OwningTeamId</c> to match against).
    /// </summary>
    private IReadOnlyList<AccessibleWorkItem> FindTeamWorkItems(
        ServiceRequest instance, ServiceBlueprint definition, ActorProfile accessProfile, string teamId)
    {
        var items = new List<AccessibleWorkItem>();

        if (instance.Cursors.Count == 0)
        {
            var stage = definition.Stages.FirstOrDefault(candidate =>
                string.Equals(candidate.StageKey, instance.CurrentStage, StringComparison.Ordinal));

            if (stage is not null)
            {
                var queueKey = GetQueueKey(stage);
                var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, queueKey, StringComparison.Ordinal));
                if (string.Equals(queueDef?.OwningTeamId, teamId, StringComparison.Ordinal))
                {
                    var queueName = ResolveQueueName(definition, stage);
                    var (assignedTo, assignedTeamId) = ResolveQueueOwnership(queueDef, instance, queueKey, cursorAssignedTo: null);
                    var eligibleActions = workItems.BuildEligibleActions(instance, definition, stage.StageKey, queueName, accessProfile);

                    items.Add(new AccessibleWorkItem(
                        stage.StageKey,
                        stage.DisplayName,
                        queueName,
                        queueKey,
                        IsJoinGateway: false,
                        eligibleActions,
                        assignedTo is not null ? eligibleActions : [],
                        queueDef?.AssignmentPolicy,
                        CursorId: RequestCursor.PrimaryCursorId,
                        AssignedTo: assignedTo,
                        AssignedTeamId: assignedTeamId));
                }
            }

            return items;
        }

        foreach (var cursor in instance.Cursors.Where(candidate => !candidate.IsAtGateway))
        {
            var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, cursor.QueueKey, StringComparison.Ordinal));
            if (!string.Equals(queueDef?.OwningTeamId, teamId, StringComparison.Ordinal))
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
            var (assignedTo, assignedTeamId) = ResolveQueueOwnership(queueDef, instance, cursor.QueueKey, cursor.AssignedTo);
            var eligibleActions = workItems.BuildEligibleActions(instance, definition, stage.StageKey, queueName, accessProfile);

            items.Add(new AccessibleWorkItem(
                stage.StageKey,
                stage.DisplayName,
                queueName,
                cursor.QueueKey,
                IsJoinGateway: false,
                eligibleActions,
                assignedTo is not null ? eligibleActions : [],
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

            var queueDef = GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, gateway.QueueKey, StringComparison.Ordinal));
            if (!string.Equals(queueDef?.OwningTeamId, teamId, StringComparison.Ordinal))
            {
                continue;
            }

            var queueName = ResolveQueueName(definition, gateway);
            items.Add(new AccessibleWorkItem(
                gateway.Key,
                gateway.DisplayName,
                queueName,
                gateway.QueueKey,
                IsJoinGateway: true,
                [],
                [],
                queueDef!.AssignmentPolicy,
                CursorId: cursor.CursorId,
                AssignedTo: null,
                AssignedTeamId: teamId));
        }

        return items
            .OrderByDescending(item => string.Equals(item.StageKey, instance.CurrentStage, StringComparison.Ordinal))
            .ThenBy(item => item.IsJoinGateway)
            .ThenBy(item => item.StageKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The <see cref="GetQueueWorkItems"/> counterpart to <see cref="BuildEnvelope"/>'s own
    /// poll-resolve step (see its remarks) — same check, same
    /// <c>TryPollResolveSupportSystemInvocations</c> call, just reachable from the list
    /// view instead of only a single instance's own page. Only fires for an instance whose
    /// visible item is actually a join gateway the accessing actor is waiting behind; every
    /// other row is returned untouched.
    /// </summary>
    private ServiceRequest RefreshIfWaitingAtJoin(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile)
    {
        var visibleItem = workItems.FindAccessibleWorkItems(instance, definition, accessProfile) is [var firstItem, ..] ? firstItem : null;
        if (visibleItem is not { IsJoinGateway: true })
        {
            return instance;
        }

        var joinGateway = FindGateway(definition, visibleItem.StageKey);
        if (joinGateway is null
            || !tryPollSupportSystems(instance, definition, joinGateway)
            || !instances.TryGet(instance.InstanceId, out var refreshed))
        {
            return instance;
        }

        return refreshed;
    }
}
