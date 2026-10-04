using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Validation;
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.FieldValueMerge;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Moves an instance on when an actor takes an action: checks access and version, validates what was submitted
/// against the stage that was actually rendered, then follows the route — into a gateway, across queues, or
/// plainly to the next stage. Nothing is saved unless the version still matches at the moment of the write.
/// </summary>
internal sealed partial class InstanceAdvancer(
    BlueprintRegistry registry,
    InstanceRepository instances,
    WorkItemFinder workItems,
    StageCalculations calculations,
    StageRenderer renderer,
    EnvelopeBuilder envelopes,
    GatewayAdvancer gateways,
    SupportSystemActions supportSystems,
    ILogger logger)
{
    /// <summary>The caller and the request being advanced.</summary>
    private readonly record struct Request(
        ServiceRequest Instance,
        ServiceBlueprint Definition,
        ActorProfile AccessProfile,
        string UserId,
        string Action,
        int ExpectedStateVersion,
        Dictionary<string, object?>? FieldValues);

    public ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues)
    {
        if (!instances.TryGet(instanceId, out var instance))
        {
            return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
        }

        if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
        {
            return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
        }

        if (instance.StateVersion != expectedStateVersion)
        {
            return Envelopes.Error(
                $"State version mismatch: expected {expectedStateVersion}, actual {instance.StateVersion}.",
                "VERSION_MISMATCH");
        }

        if (instance.IsAborted)
        {
            return Envelopes.Aborted(instance);
        }

        if (!registry.TryGet(instance.BlueprintKey, out var definition))
        {
            return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
        }

        var request = new Request(instance, definition, accessProfile, userId, action, expectedStateVersion, fieldValues);

        return action.StartsWith("change:", StringComparison.OrdinalIgnoreCase)
            ? JumpToStage(request, action["change:".Length..])
            : TakeRoute(request);
    }

    /// <summary>An administrator's "change" link: move the instance straight to another stage.</summary>
    private ServiceRequestResponseEnvelope JumpToStage(Request request, string targetStageKey)
    {
        var (instance, definition, accessProfile, userId, action, expectedStateVersion, _) = request;

        var targetStage = definition.Stages.FirstOrDefault(s => s.StageKey == targetStageKey);
        if (targetStage is null)
        {
            return Envelopes.Error($"State '{targetStageKey}' not found in definition.", "STATE_NOT_FOUND");
        }

        // FindAccessibleWorkItems (called when the response is built) renders from instance.Cursors, not
        // instance.CurrentStage, the moment ANY cursor exists — which is every blueprint that has passed a
        // gateway. Updating only CurrentStage left a "change:" jump a silent no-op past the first stage.
        // Move whichever active, non-gateway cursor belongs to the target stage's own queue — the same cursor
        // a normal forward Advance would move — so the jump actually takes.
        var updatedCursors = instance.Cursors.Count == 0
            ? instance.Cursors
            : GatewayAdvancer.MoveCursor(
                instance.Cursors,
                instance.Cursors.FirstOrDefault(c => !c.IsAtGateway && c.QueueKey == GetQueueKey(targetStage))?.CursorId,
                targetStageKey,
                isAtGateway: false);

        var jumped = instance with
        {
            CurrentStage = targetStageKey,
            Cursors = updatedCursors,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var audit = AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: instance.CurrentStage, toStageKey: targetStageKey, action: action, detail: "admin change-link jump");
        var saved = instances.TrySaveIfVersionMatches(jumped, userId, instance.StateVersion, audit);
        if (saved is null)
        {
            return ConcurrentUpdate(expectedStateVersion);
        }

        ChangeLinkJumped(logger, instance.InstanceId, targetStageKey);
        return envelopes.BuildEnvelope(saved, definition, accessProfile, userId);
    }

    private ServiceRequestResponseEnvelope TakeRoute(Request request)
    {
        var (instance, definition, accessProfile, userId, action, _, fieldValues) = request;

        var visibleWorkItem = workItems.FindAccessibleWorkItems(instance, definition, accessProfile, userId)
            .FirstOrDefault(item => item.AvailableActions.Any(candidate =>
                string.Equals(candidate.ActionKey, action, StringComparison.Ordinal)));

        if (visibleWorkItem is null)
        {
            return Envelopes.Error(
                $"Action '{action}' is not valid from the current queue view.",
                "INVALID_TRANSITION");
        }

        var transition = GetOutgoingTransitions(definition, visibleWorkItem.StageKey).FirstOrDefault(
            t => t.FromState == visibleWorkItem.StageKey
                 && t.Action == action);

        if (transition == null)
        {
            return Envelopes.Error(
                $"Action '{action}' is not valid from stage '{visibleWorkItem.StageKey}'.",
                "INVALID_TRANSITION");
        }

        if (ValidateSubmission(request, visibleWorkItem.StageKey) is { } rejected)
        {
            return rejected;
        }

        // Is the target a gateway rather than a plain stage?
        var nextGateway = FindGateway(definition, transition.ToState);
        if (nextGateway != null)
        {
            return nextGateway.GatewayType == GatewayKind.Split
                ? gateways.HandleSplitGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId)
                : gateways.HandleJoinGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId);
        }

        return instance.Cursors.Count > 0
            ? MoveCursorToStage(request, visibleWorkItem.StageKey, transition)
            : MoveToStage(request, visibleWorkItem.StageKey, transition);
    }

    /// <summary>
    /// Never trust the client: whatever arrived — from a legitimate form post or a tampered one — is validated against
    /// the CURRENT stage's own authoritative field declarations before anything touches instance state. It reuses the
    /// exact methods rendering calls for this stage, so validation cannot drift from what was rendered. Returns the
    /// response to send back if the submission is rejected (re-rendering what was typed), or null if it is acceptable.
    /// </summary>
    private ServiceRequestResponseEnvelope? ValidateSubmission(Request request, string currentStageKey)
    {
        var (instance, definition, accessProfile, userId, action, _, fieldValues) = request;

        var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == currentStageKey);
        if (currentStage is null)
        {
            return null;
        }

        var currentCalc = calculations.EvaluateDefinitionCalculations(instance, definition, currentStage);
        var currentComponents = renderer.BuildComponents(currentStage.Components, instance.FieldValues, currentCalc);
        var authoritativeFields = currentComponents.SelectMany(c => c.Fields).ToArray();
        var hiddenFieldKeys = currentComponents
            .Where(c => c.Hidden)
            .SelectMany(c => c.Fields)
            .Select(f => f.FieldKey)
            .ToHashSet(StringComparer.Ordinal);

        // A host may legitimately omit a field from fieldValues entirely — a file-upload field the visitor didn't
        // re-select is the case that happens (browsers can never pre-fill a file input), relying on this stage's
        // already-persisted values to satisfy Required instead. Only this stage's OWN field keys may backfill from
        // instance.FieldValues; anything else belongs to a different stage and must stay out, or the whitelist check
        // would reject it as an unknown field.
        var currentStageFieldKeys = authoritativeFields.Select(f => f.FieldKey).ToHashSet(StringComparer.Ordinal);
        var existingForCurrentStage = instance.FieldValues
            .Where(kvp => currentStageFieldKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        var submittedStrings = Merge(existingForCurrentStage, fieldValues)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

        var validation = FieldValueValidator.Validate(authoritativeFields, submittedStrings, hiddenFieldKeys);
        if (!validation.IsValid)
        {
            var problems = validation.Errors
                .Select(e => new ServiceRequestProblem { FieldKey = e.Key, Message = e.Value, Code = "VALIDATION_ERROR" })
                .ToArray();
            return RenderWithProblems(request, problems);
        }

        // Declarative cross-field business rules, checked once field-level validation has passed, on the same merge of
        // persisted + just-submitted values — never on stale persisted data or anything the client claims was pre-checked.
        var stageValidationProblems = calculations.EvaluateStageValidations(instance, definition, currentStage, fieldValues, action);
        return stageValidationProblems.Count > 0
            ? RenderWithProblems(request, stageValidationProblems)
            : null;
    }

    /// <summary>
    /// Render with what was just submitted, not the persisted instance. A rejected submission is never saved, so
    /// StateVersion and the store stay untouched, but the re-render must still reflect it: rendering from the
    /// unmodified instance would blank every field on this stage back to what it was before the user started typing.
    /// </summary>
    private ServiceRequestResponseEnvelope RenderWithProblems(Request request, IReadOnlyList<ServiceRequestProblem> problems)
    {
        var preview = request.Instance with { FieldValues = Merge(request.Instance.FieldValues, request.FieldValues) };
        return envelopes.BuildEnvelope(preview, request.Definition, request.AccessProfile, request.UserId) with { Problems = problems };
    }

    /// <summary>
    /// A plain hop on an instance that has cursors: advance only the cursor at this stage. A hop can cross into a
    /// differently-queued stage with no gateway in between, so the cursor's own QueueKey must follow it there, or the
    /// work-item lookup (which resolves ownership and eligibility from cursor.QueueKey) keeps checking the stage the
    /// cursor just left.
    /// </summary>
    private ServiceRequestResponseEnvelope MoveCursorToStage(Request request, string fromStageKey, RouteFile transition)
    {
        var (instance, definition, accessProfile, userId, _, expectedStateVersion, fieldValues) = request;

        var sourceCursor = instance.Cursors.FirstOrDefault(c => c.CurrentNodeKey == fromStageKey && !c.IsAtGateway);
        var targetStage = definition.Stages.FirstOrDefault(s => s.StageKey == transition.ToState);
        var updatedCursors = GatewayAdvancer.MoveCursor(
            instance.Cursors, sourceCursor?.CursorId, transition.ToState, isAtGateway: false,
            newQueueKey: GetQueueKey(targetStage));
        var primaryStage = GatewayAdvancer.FirstActiveStageCursorKey(updatedCursors) ?? transition.ToState;
        var mergedFieldValues = Merge(instance.FieldValues, fieldValues);
        var movedCursor = updatedCursors.FirstOrDefault(c => c.CursorId == sourceCursor?.CursorId);
        var newInvocations = movedCursor is not null
            ? supportSystems.ExecuteOnEnterSupportSystemActions(instance.InstanceId, definition, mergedFieldValues, movedCursor)
            : [];
        var updated = instance with
        {
            CurrentStage = primaryStage,
            Cursors = updatedCursors,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = mergedFieldValues,
            SupportSystemInvocations = instance.SupportSystemInvocations.Concat(newInvocations).ToArray()
        };
        var audit = AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: sourceCursor?.CursorId,
            fromStageKey: fromStageKey, toStageKey: transition.ToState, action: transition.Action);
        var saved = instances.TrySaveIfVersionMatches(updated, userId, instance.StateVersion, audit);
        if (saved is null)
        {
            return ConcurrentUpdate(expectedStateVersion);
        }

        MultiCursorAdvanced(logger, instance.InstanceId, sourceCursor?.CursorId ?? "(none)", transition.ToState);
        return envelopes.BuildEnvelope(saved, definition, accessProfile, userId);
    }

    /// <summary>A plain hop on an instance with no cursors yet.</summary>
    private ServiceRequestResponseEnvelope MoveToStage(Request request, string fromStageKey, RouteFile transition)
    {
        var (instance, definition, accessProfile, userId, _, expectedStateVersion, fieldValues) = request;

        var updated = instance with
        {
            CurrentStage = transition.ToState,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = Merge(instance.FieldValues, fieldValues)
        };

        var audit = AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: fromStageKey, toStageKey: transition.ToState, action: transition.Action);
        var saved = instances.TrySaveIfVersionMatches(updated, userId, instance.StateVersion, audit);
        if (saved is null)
        {
            return ConcurrentUpdate(expectedStateVersion);
        }

        Advanced(logger, instance.InstanceId, fromStageKey, transition.ToState);
        return envelopes.BuildEnvelope(saved, definition, accessProfile, userId);
    }

    private static ServiceRequestResponseEnvelope ConcurrentUpdate(int expectedStateVersion) =>
        Envelopes.Error(
            $"State version mismatch: expected {expectedStateVersion}, actual has changed concurrently.",
            "VERSION_MISMATCH");

    [LoggerMessage(Level = LogLevel.Information, Message = "Change-link: jumped instance {Id} to stage '{State}'")]
    private static partial void ChangeLinkJumped(ILogger logger, string id, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Multi-cursor advance instance {Id}: cursor {CursorId} → {To}")]
    private static partial void MultiCursorAdvanced(ILogger logger, string id, string cursorId, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Advanced instance {Id}: {From} → {To}")]
    private static partial void Advanced(ILogger logger, string id, string from, string to);
}
