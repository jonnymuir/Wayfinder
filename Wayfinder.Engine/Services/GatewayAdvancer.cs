using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.FieldValueMerge;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Advances an instance through Split gateways (fan out into one cursor per branch) and Join gateways
/// (wait for every required queue, then release). See docs/guides/parallel-work.md.
/// </summary>
internal sealed partial class GatewayAdvancer(
    InstanceRepository instances,
    BulkDatasetActions bulkDatasets,
    SupportSystemActions supportSystems,
    IEnvelopeSource envelopes,
    ILogger logger)
{
    public ServiceRequestResponseEnvelope HandleSplitGatewayAdvance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        RouteFile arrivingTransition,
        ServiceBlueprintGatewayDefinition splitGateway,
        Dictionary<string, object?>? fieldValues,
        ActorProfile accessProfile,
        string userId)
    {
        // Find all outgoing branches from the split gateway.
        // Split gateway transitions carry the action "split-auto" by convention or any action
        // — we follow ALL outgoing transitions from the gateway deterministically.
        var outgoing = GetOutgoingTransitions(definition, splitGateway.Key)
            .Where(transition => string.Equals(transition.Action, arrivingTransition.Action, StringComparison.Ordinal)
                || string.Equals(transition.Action, "split-auto", StringComparison.Ordinal))
            .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
            .ToList();

        if (outgoing.Count == 0)
        {
            outgoing = GetOutgoingTransitions(definition, splitGateway.Key)
                .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
                .ToList();
        }

        if (outgoing.Count == 0)
        {
            return Envelopes.Error(
                $"Split gateway '{splitGateway.Key}' has no outgoing transitions.",
                "GATEWAY_NO_OUTGOING");
        }

        // Identify the cursor being advanced (if we are already in multi-cursor mode).
        var sourceCursor = instance.Cursors
            .FirstOrDefault(c => c.CurrentNodeKey == arrivingTransition.FromState && !c.IsAtGateway);
        var sourceCursorId = sourceCursor?.CursorId;

        // Remove the arriving cursor (or primary stage in single-cursor mode) and fan out.
        var remainingCursors = sourceCursorId != null
            ? instance.Cursors.Where(c => c.CursorId != sourceCursorId).ToList()
            : new List<RequestCursor>();

        var newCursors = outgoing.Select(t =>
        {
            var targetGateway = FindGateway(definition, t.ToState);
            var targetQueueKey = FirstNonEmpty(
                targetGateway?.GatewayType == GatewayKind.Join
                    ? sourceCursor?.QueueKey
                    : targetGateway?.QueueKey,
                GetQueueKey(definition.Stages.FirstOrDefault(stage => stage.StageKey == t.ToState)),
                sourceCursor?.QueueKey,
                splitGateway.QueueKey);

            return new RequestCursor
            {
                CursorId = Guid.NewGuid().ToString(),
                QueueKey = targetQueueKey ?? string.Empty,
                CurrentNodeKey = t.ToState,
                IsAtGateway = targetGateway != null,
                ArrivedViaAction = targetGateway != null ? t.Action : null
            };
        }).ToList();

        var allCursors = remainingCursors.Concat(newCursors).ToArray();
        var primaryStage = FirstActiveStageCursorKey(allCursors) ?? newCursors[0].CurrentNodeKey;
        var joinArrivals = new Dictionary<string, IReadOnlyList<string>>(instance.JoinArrivals);
        var mergedFieldValues = Merge(instance.FieldValues, fieldValues);

        // A branch that lands straight on a stage (not another gateway) may carry its own
        // onEnter support-system-call action — the automation-queue branch of a "send to
        // support system" split, e.g. See ExecuteOnEnterSupportSystemActions's own remarks for
        // why this only runs for multi-cursor branches, not the single-cursor path. Bulk-dataset
        // actions run first, per cursor, so a bulk-dataset-materialize action's refreshed file
        // (a resubmission loop re-firing this same split) is what support-system-call reads —
        // see ExecuteOnEnterBulkDatasetActions's own remarks.
        var newInvocations = new List<SupportSystemInvocation>();
        foreach (var cursor in newCursors.Where(cursor => !cursor.IsAtGateway))
        {
            var bulkDatasetUpdates = bulkDatasets.ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, mergedFieldValues, cursor);
            if (bulkDatasetUpdates.Count > 0)
            {
                mergedFieldValues = Merge(mergedFieldValues, bulkDatasetUpdates);
            }

            newInvocations.AddRange(supportSystems.ExecuteOnEnterSupportSystemActions(instance.InstanceId, definition, mergedFieldValues, cursor));
        }

        foreach (var joinGroup in newCursors
                     .Where(cursor => cursor.IsAtGateway)
                     .GroupBy(cursor => cursor.CurrentNodeKey, StringComparer.Ordinal))
        {
            var gateway = FindGateway(definition, joinGroup.Key);
            if (gateway?.GatewayType != GatewayKind.Join)
                continue;

            var existingArrivals = joinArrivals.TryGetValue(joinGroup.Key, out var existing)
                ? existing.ToList()
                : new List<string>();

            foreach (var cursorId in joinGroup.Select(cursor => cursor.CursorId))
            {
                if (!existingArrivals.Contains(cursorId))
                    existingArrivals.Add(cursorId);
            }

            joinArrivals[joinGroup.Key] = existingArrivals;
        }

        var updated = instance with
        {
            CurrentStage = primaryStage,
            Cursors = allCursors,
            JoinArrivals = joinArrivals,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = mergedFieldValues,
            SupportSystemInvocations = instance.SupportSystemInvocations.Concat(newInvocations).ToArray()
        };

        foreach (var joinKey in newCursors
                     .Where(cursor => cursor.IsAtGateway)
                     .Select(cursor => cursor.CurrentNodeKey)
                     .Distinct(StringComparer.Ordinal))
        {
            var joinGateway = FindGateway(definition, joinKey);
            if (joinGateway is null)
            {
                continue;
            }

            if (TryReleaseJoinIfReady(updated, definition, joinGateway, accessProfile, userId) is { } released)
            {
                return released;
            }
        }

        updated = instances.Save(updated, userId, AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: arrivingTransition.FromState, toStageKey: splitGateway.Key, action: arrivingTransition.Action,
            detail: $"split gateway fanned out to {newCursors.Count} cursors"));
        SplitFannedOut(logger, splitGateway.Key, instance.InstanceId, newCursors.Count);

        return envelopes.BuildEnvelope(updated, definition, accessProfile, userId);
    }

    public ServiceRequestResponseEnvelope HandleJoinGatewayAdvance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        RouteFile arrivingTransition,
        ServiceBlueprintGatewayDefinition joinGateway,
        Dictionary<string, object?>? fieldValues,
        ActorProfile accessProfile,
        string userId)
    {
        var gatewayKey = joinGateway.Key;
        var requiredQueues = joinGateway.RequiredIncomingQueues ?? [];

        // Identify the arriving cursor.
        var arrivingCursor = instance.Cursors.Count > 0
            ? instance.Cursors.FirstOrDefault(c => c.CurrentNodeKey == arrivingTransition.FromState && !c.IsAtGateway)
            : new RequestCursor
            {
                CursorId = Guid.NewGuid().ToString(),
                QueueKey = FirstNonEmpty(
                               GetQueueKey(definition.Stages.FirstOrDefault(stage => stage.StageKey == arrivingTransition.FromState)),
                               joinGateway.QueueKey)
                           ?? string.Empty,
                CurrentNodeKey = arrivingTransition.FromState,
                IsAtGateway = false
            };

        var arrivingCursorId = arrivingCursor?.CursorId ?? Guid.NewGuid().ToString();
        var arrivingQueueKey = FirstNonEmpty(arrivingCursor?.QueueKey, joinGateway.QueueKey) ?? string.Empty;

        // Record arrival in join token bookkeeping.
        var existingArrivals = instance.JoinArrivals.TryGetValue(gatewayKey, out var existing)
            ? existing.ToList()
            : new List<string>();

        if (!existingArrivals.Contains(arrivingCursorId))
            existingArrivals.Add(arrivingCursorId);

        // Move the arriving cursor to the join gateway.
        var cursorsAfterArrival = instance.Cursors.Count > 0
            ? MoveCursor(instance.Cursors, arrivingCursor?.CursorId, gatewayKey, isAtGateway: true, arrivedViaAction: arrivingTransition.Action)
            : [new RequestCursor { CursorId = arrivingCursorId, QueueKey = arrivingQueueKey, CurrentNodeKey = gatewayKey, IsAtGateway = true, ArrivedViaAction = arrivingTransition.Action }];

        var updatedArrivals = new Dictionary<string, IReadOnlyList<string>>(instance.JoinArrivals)
        {
            [gatewayKey] = existingArrivals
        };

        // Check if all required queues have a cursor at this gateway.
        var arrivedCursorIds = new HashSet<string>(existingArrivals, StringComparer.Ordinal);
        var arrivedQueues = cursorsAfterArrival
            .Where(c => c.IsAtGateway && string.Equals(c.CurrentNodeKey, gatewayKey, StringComparison.Ordinal)
                                      && arrivedCursorIds.Contains(c.CursorId))
            .Select(c => c.QueueKey)
            .ToHashSet(StringComparer.Ordinal);

        var allRequiredArrived = requiredQueues.Count == 0 || requiredQueues.All(queue => arrivedQueues.Contains(queue));

        if (!allRequiredArrived)
        {
            // Waiting — record arrival but do not release.
            var waitingInstance = instance with
            {
                CurrentStage = FirstActiveStageCursorKey(cursorsAfterArrival) ?? gatewayKey,
                Cursors = cursorsAfterArrival,
                JoinArrivals = updatedArrivals,
                StateVersion = instance.StateVersion + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                FieldValues = Merge(instance.FieldValues, fieldValues)
            };

            waitingInstance = instances.Save(waitingInstance, userId, AuditEvents.Transition(
                instance.InstanceId, userId, cursorId: arrivingCursorId,
                fromStageKey: arrivingTransition.FromState, toStageKey: gatewayKey, action: arrivingTransition.Action,
                detail: $"arrived at join, waiting ({arrivedQueues.Count}/{requiredQueues.Count} queues)"));
            JoinWaiting(logger, gatewayKey, instance.InstanceId, arrivedQueues.Count, requiredQueues.Count);

            return Envelopes.JoinWaiting(waitingInstance, definition, joinGateway);
        }

        var arrivedInstance = instance with
        {
            CurrentStage = FirstActiveStageCursorKey(cursorsAfterArrival) ?? gatewayKey,
            Cursors = cursorsAfterArrival,
            JoinArrivals = updatedArrivals,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = Merge(instance.FieldValues, fieldValues)
        };

        return TryReleaseJoinIfReady(arrivedInstance, definition, joinGateway, accessProfile, userId)
               ?? Envelopes.JoinWaiting(arrivedInstance, definition, joinGateway);
    }

    private ServiceRequestResponseEnvelope? TryReleaseJoinIfReady(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ServiceBlueprintGatewayDefinition joinGateway,
        ActorProfile accessProfile,
        string userId)
    {
        var gatewayKey = joinGateway.Key;
        var requiredQueues = joinGateway.RequiredIncomingQueues ?? [];
        var arrivedCursorIds = instance.JoinArrivals.TryGetValue(gatewayKey, out var arrivals)
            ? new HashSet<string>(arrivals, StringComparer.Ordinal)
            : [];
        var arrivedQueues = instance.Cursors
            .Where(cursor => cursor.IsAtGateway
                && string.Equals(cursor.CurrentNodeKey, gatewayKey, StringComparison.Ordinal)
                && arrivedCursorIds.Contains(cursor.CursorId))
            .Select(cursor => cursor.QueueKey)
            .ToHashSet(StringComparer.Ordinal);

        if (requiredQueues.Count > 0 && !requiredQueues.All(queue => arrivedQueues.Contains(queue)))
        {
            return null;
        }

        var outgoing = GetOutgoingTransitions(definition, gatewayKey)
            .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
            .ToList();

        if (outgoing.Count == 0)
        {
            return Envelopes.Error(
                $"Join gateway '{gatewayKey}' has no outgoing transitions.",
                "GATEWAY_NO_OUTGOING");
        }

        // A join with a single outgoing route always fires it — unchanged from before this
        // gateway could branch at all. A join with more than one outgoing route picks the one
        // whose trigger matches the action that produced one of the cursors now parked here (e.g.
        // "approve" vs "reject") instead of firing every route, which is what let two cursors —
        // one per branch — leak out of what's supposed to be a single decision point.
        var selectedOutgoing = outgoing;
        if (outgoing.Count > 1)
        {
            var arrivedActions = instance.Cursors
                .Where(cursor => cursor.IsAtGateway
                    && string.Equals(cursor.CurrentNodeKey, gatewayKey, StringComparison.Ordinal)
                    && arrivedCursorIds.Contains(cursor.CursorId)
                    && !string.IsNullOrWhiteSpace(cursor.ArrivedViaAction))
                .Select(cursor => cursor.ArrivedViaAction!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var matches = outgoing
                .Where(transition => arrivedActions.Contains(transition.Action, StringComparer.Ordinal))
                .ToList();

            if (matches.Count != 1)
            {
                return Envelopes.Error(
                    $"Join gateway '{gatewayKey}' has {outgoing.Count} outgoing routes but could not " +
                    $"determine which to take (arrived actions: [{string.Join(", ", arrivedActions)}], " +
                    $"matched {matches.Count} route(s)). Exactly one outgoing route's trigger must match " +
                    "exactly one arrived action.",
                    "GATEWAY_AMBIGUOUS_JOIN_ROUTE");
            }

            selectedOutgoing = matches;
        }

        var cursorsWithoutJoin = instance.Cursors
            .Where(cursor => !(cursor.IsAtGateway && string.Equals(cursor.CurrentNodeKey, gatewayKey, StringComparison.Ordinal)))
            .ToList();

        var releaseCursors = selectedOutgoing.Select(transition =>
        {
            var targetGateway = FindGateway(definition, transition.ToState);
            return new RequestCursor
            {
                CursorId = Guid.NewGuid().ToString(),
                QueueKey = FirstNonEmpty(
                               targetGateway?.QueueKey,
                               GetQueueKey(definition.Stages.FirstOrDefault(stage => stage.StageKey == transition.ToState)),
                               joinGateway.QueueKey)
                           ?? string.Empty,
                CurrentNodeKey = transition.ToState,
                IsAtGateway = targetGateway != null,
                ArrivedViaAction = targetGateway != null ? transition.Action : null
            };
        }).ToList();

        var releasedCursors = cursorsWithoutJoin.Concat(releaseCursors).ToArray();
        var cleanedArrivals = new Dictionary<string, IReadOnlyList<string>>(instance.JoinArrivals);
        cleanedArrivals.Remove(gatewayKey);

        // A release that lands straight on a stage (not another gateway) may carry its own
        // onEnter bulk-dataset-ingest action — the review stage of a bulk-data flow, reached the
        // moment its automation branch's support-system-call resolves and this join releases.
        // See ExecuteOnEnterBulkDatasetActions's own remarks.
        var releasedFieldValues = instance.FieldValues;
        foreach (var cursor in releaseCursors.Where(cursor => !cursor.IsAtGateway))
        {
            var bulkDatasetUpdates = bulkDatasets.ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, releasedFieldValues, cursor);
            if (bulkDatasetUpdates.Count > 0)
            {
                releasedFieldValues = Merge(releasedFieldValues, bulkDatasetUpdates);
            }
        }

        var releasedInstance = instance with
        {
            CurrentStage = FirstActiveStageCursorKey(releasedCursors) ?? selectedOutgoing[0].ToState,
            Cursors = releasedCursors,
            JoinArrivals = cleanedArrivals,
            FieldValues = releasedFieldValues
        };

        releasedInstance = instances.Save(releasedInstance, userId, AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: gatewayKey, toStageKey: selectedOutgoing[0].ToState, action: selectedOutgoing[0].Action,
            detail: "join released"));
        return envelopes.BuildEnvelope(releasedInstance, definition, accessProfile, userId);
    }

    public static IReadOnlyList<RequestCursor> MoveCursor(
        IReadOnlyList<RequestCursor> cursors,
        string? cursorId,
        string newNodeKey,
        bool isAtGateway,
        string? arrivedViaAction = null,
        string? newQueueKey = null)
    {
        if (cursorId == null)
            return cursors;

        return cursors
            .Select(c => c.CursorId == cursorId
                ? c with
                {
                    CurrentNodeKey = newNodeKey,
                    IsAtGateway = isAtGateway,
                    ArrivedViaAction = isAtGateway ? arrivedViaAction : null,
                    QueueKey = newQueueKey ?? c.QueueKey
                }
                : c)
            .ToArray();
    }

    public static string? FirstActiveStageCursorKey(IReadOnlyList<RequestCursor> cursors) =>
        cursors.FirstOrDefault(c => !c.IsAtGateway)?.CurrentNodeKey;

    [LoggerMessage(Level = LogLevel.Information, Message = "Split gateway '{Gateway}': instance {Id} fanned out to {Count} cursors.")]
    private static partial void SplitFannedOut(ILogger logger, string gateway, string id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Join gateway '{Gateway}': instance {Id} waiting ({Arrived}/{Required} queues).")]
    private static partial void JoinWaiting(ILogger logger, string gateway, string id, int arrived, int required);
}
