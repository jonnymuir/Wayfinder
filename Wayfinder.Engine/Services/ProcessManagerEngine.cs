using System.Text.Json;
using System.Text.Json.Nodes;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;
using Microsoft.Extensions.Logging;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Services.Sanitization;
using Wayfinder.Services.Validation;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign.BulkData;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Generic in-memory runtime engine that executes Wayfinder service blueprints.
/// </summary>
public class ProcessManagerEngine : IProcessManager
{
    private readonly IServiceContentSanitizer _sanitizer;
    private readonly Dictionary<string, ServiceBlueprint> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceRequestStore _instanceStore;
    private readonly Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?>? _serviceInputsResolver;
    private readonly IReadOnlyDictionary<string, ISupportSystemClient> _supportSystemClients;
    private readonly IBulkDatasetStore? _bulkDatasetStore;

    public ProcessManagerEngine(
        ILogger logger,
        IServiceBlueprintStore definitionStore,
        IServiceContentSanitizer sanitizer,
        Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?>? serviceInputsResolver = null,
        IServiceRequestStore? instanceStore = null,
        IEnumerable<ISupportSystemClient>? supportSystemClients = null,
        IBulkDatasetStore? bulkDatasetStore = null)
    {
        Logger = logger;
        _sanitizer = sanitizer;
        _serviceInputsResolver = serviceInputsResolver;
        _instanceStore = instanceStore ?? new InMemoryServiceRequestStore();
        _supportSystemClients = (supportSystemClients ?? [])
            .ToDictionary(client => client.SupportSystemKey, StringComparer.Ordinal);
        _bulkDatasetStore = bulkDatasetStore;

        foreach (var (lookupKey, definition) in definitionStore.LoadDefinitions(logger))
        {
            var runtimeLookupKey = !string.IsNullOrWhiteSpace(lookupKey)
                ? lookupKey
                : definition.DefinitionKey;

            if (!string.IsNullOrWhiteSpace(runtimeLookupKey))
            {
                _definitions[runtimeLookupKey] = definition;
            }
        }

        Logger.LogInformation("Blueprint runtime ready: {Defs} definition(s).", _definitions.Count);
    }

    protected ILogger Logger { get; }

    public ServiceRequestResponseEnvelope GetCurrent(
        string blueprintKey,
        string tenantId,
        string userId,
        string? instanceId = null,
        string? action = null) =>
        GetCurrent(
            blueprintKey,
            tenantId,
            userId,
            ActorProfile.UnrestrictedOwner,
            instanceId,
            action);

    public ServiceRequestResponseEnvelope GetCurrent(
        string blueprintKey,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string? instanceId = null,
        string? action = null)
    {
        if (!_definitions.TryGetValue(blueprintKey, out var definition))
        {
            Logger.LogWarning("Service blueprint not found: {Key}", blueprintKey);
            return ErrorEnvelope(
                $"Blueprint '{blueprintKey}' is not registered with this application.",
                "DEFINITION_NOT_FOUND");
        }

        if (!string.IsNullOrEmpty(instanceId))
        {
            if (!_instanceStore.TryGet(instanceId, out var specificInstance))
            {
                return ErrorEnvelope($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(specificInstance, tenantId, userId, accessProfile))
            {
                return ErrorEnvelope("Access denied to this service request.", "ACCESS_DENIED");
            }

            Logger.LogInformation("Resuming specific instance {Id}", instanceId);
            return BuildEnvelope(specificInstance, definition, accessProfile);
        }

        var existingInstance = FindLatestInstance(tenantId, userId, blueprintKey);

        if (!CanStartInitialState(definition, accessProfile))
        {
            return ErrorEnvelope("Access denied to start this queue.", "ACCESS_DENIED");
        }

        if (string.Equals(action, "start-new", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "Created new service request {Id} for key={Key} (action=start-new)",
                blueprintKey);
        }

        if (string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
        {
            if (existingInstance is not null)
            {
                Logger.LogInformation("Resuming existing instance {Id} (action=resume)", existingInstance.InstanceId);
                return BuildEnvelope(existingInstance, definition, accessProfile);
            }

            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "Created service request {Id} for key={Key} (action=resume, no existing)",
                blueprintKey);
        }

        var policy = definition.RequestPolicy;

        if (string.Equals(policy, "multiple", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "Created new service request {Id} for key={Key} (policy=multiple)",
                blueprintKey);
        }

        if (string.Equals(policy, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (existingInstance is not null)
            {
                var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == existingInstance.CurrentStage);

                if (!IsTerminalInstance(existingInstance, definition))
                {
                    Logger.LogInformation(
                        "Active instance {Id} exists for key={Key}; returning instance_picker",
                        existingInstance.InstanceId,
                        blueprintKey);

                    return new ServiceRequestResponseEnvelope
                    {
                        InstanceId = existingInstance.InstanceId,
                        ResponseState = "instance_picker",
                        StateVersion = existingInstance.StateVersion,
                        CorrelationId = existingInstance.InstanceId,
                        ServerTimeUtc = DateTimeOffset.UtcNow,
                        RequestPolicy = "prompt",
                        Render = new StepContent
                        {
                            StepType = currentStage?.Components.InferStepType() ?? "question",
                            StateDisplayName = currentStage?.DisplayName ?? definition.DisplayName,
                            Components = Array.Empty<ComponentRenderPayload>(),
                            AvailableActions = Array.Empty<ServiceRequestAction>()
                        }
                    };
                }
            }

            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "Created service request {Id} for key={Key} (policy=prompt, no active)",
                blueprintKey);
        }

        if (existingInstance is null)
        {
            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "Created service request {Id} for key={Key} tenant={Tenant}",
                blueprintKey,
                tenantId);
        }

        // "single" means at most one instance per user for this blueprint, full stop — once it
        // reaches a terminal stage it keeps being shown on every subsequent visit (the community
        // enquiry demo depends on this: a member returning to the page sees "Thank you", not a
        // silently-reset blank form). ServiceRequestPageController's PRG redirect after a POST
        // relies on this same fallthrough to show the confirmation page for the visit that just
        // submitted it.
        return BuildEnvelope(existingInstance, definition, accessProfile);
    }

    public virtual ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues) =>
        Advance(
            instanceId,
            tenantId,
            userId,
            ActorProfile.UnrestrictedOwner,
            action,
            expectedStateVersion,
            fieldValues);

    public virtual ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues)
    {
        if (!_instanceStore.TryGet(instanceId, out var instance))
        {
            return ErrorEnvelope($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
        }

        if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
        {
            return ErrorEnvelope("Access denied to this service request.", "ACCESS_DENIED");
        }

        if (instance.StateVersion != expectedStateVersion)
        {
            return ErrorEnvelope(
                $"State version mismatch: expected {expectedStateVersion}, actual {instance.StateVersion}.",
                "VERSION_MISMATCH");
        }

        if (!_definitions.TryGetValue(instance.BlueprintKey, out var definition))
        {
            return ErrorEnvelope($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
        }

        if (action.StartsWith("change:", StringComparison.OrdinalIgnoreCase))
        {
            var targetStageKey = action["change:".Length..];
            var targetStage = definition.Stages.FirstOrDefault(s => s.StageKey == targetStageKey);
            if (targetStage is null)
            {
                return ErrorEnvelope($"State '{targetStageKey}' not found in definition.", "STATE_NOT_FOUND");
            }

            // FindAccessibleWorkItems (called by BuildEnvelope below) renders from instance.Cursors,
            // not instance.CurrentStage, the moment ANY cursor exists — which happens for every
            // blueprint that's passed through a gateway, i.e. effectively all of them, since Wayfinder
            // requires stage routes to always target a gateway. Updating only CurrentStage left a
            // "change:" jump a silent no-op past the first stage: the render kept coming from the
            // stale cursor position and the user landed right back where they started (confirmed
            // live). Move whichever active, non-gateway cursor belongs to the target stage's own
            // queue — same cursor a normal forward Advance would move — so the jump actually takes.
            var updatedCursors = instance.Cursors.Count == 0
                ? instance.Cursors
                : MoveCursor(
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

            SaveInstance(jumped);
            Logger.LogInformation(
                "Change-link: jumped instance {Id} to stage '{State}'",
                instanceId,
                targetStageKey);
            return BuildEnvelope(jumped, definition, accessProfile);
        }

        var visibleWorkItem = FindAccessibleWorkItems(instance, definition, accessProfile)
            .FirstOrDefault(item => item.AvailableActions.Any(candidate =>
                string.Equals(candidate.ActionKey, action, StringComparison.Ordinal)));

        if (visibleWorkItem is null)
        {
            return ErrorEnvelope(
                $"Action '{action}' is not valid from the current queue view.",
                "INVALID_TRANSITION");
        }

        var transition = GetOutgoingTransitions(definition, visibleWorkItem.StageKey).FirstOrDefault(
            t => t.FromState == visibleWorkItem.StageKey
                 && t.Action == action);

        if (transition == null)
        {
            return ErrorEnvelope(
                $"Action '{action}' is not valid from stage '{visibleWorkItem.StageKey}'.",
                "INVALID_TRANSITION");
        }

        // Never trust the client: whatever fieldValues arrived here — from a legitimate form
        // post or a tampered one — is validated against the CURRENT stage's own authoritative
        // field declarations before anything else touches instance state. Reuses the exact same
        // methods rendering already calls for this stage, so validation can never drift from
        // what was actually rendered, and needs no per-host wiring to be enforced.
        var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == visibleWorkItem.StageKey);
        if (currentStage is not null)
        {
            var currentCalc = EvaluateDefinitionCalculations(instance, definition, currentStage);
            var currentComponents = BuildComponents(currentStage.Components, instance.FieldValues, currentCalc);
            var authoritativeFields = currentComponents.SelectMany(c => c.Fields).ToArray();
            var hiddenFieldKeys = currentComponents
                .Where(c => c.Hidden)
                .SelectMany(c => c.Fields)
                .Select(f => f.FieldKey)
                .ToHashSet(StringComparer.Ordinal);

            // A host may legitimately omit a field from fieldValues entirely — a file-upload
            // field the visitor didn't re-select on this submission is the case that actually
            // happens (browsers can never pre-fill a file input's value, unlike every other
            // field type, so a host can't "resubmit what's already there" the way it does for
            // text/radio/date), relying on this stage's already-persisted instance.FieldValues to
            // satisfy Required instead. Only this stage's OWN field keys are eligible to backfill
            // from instance.FieldValues — anything else in there belongs to a different stage and
            // must stay out, or the whitelist check below would reject it as an unknown field.
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

                // Render with what was just submitted, not the persisted instance — a rejected
                // submission is never saved (SaveInstance isn't called here, so StateVersion and
                // the store stay untouched), but the re-render must still reflect it: this stage's
                // fields haven't been merged into instance.FieldValues yet, so rendering from the
                // unmodified instance would blank every field on this stage back to whatever was
                // there before the user started typing — not just the one that failed validation.
                var previewInstance = instance with { FieldValues = Merge(instance.FieldValues, fieldValues) };
                return BuildEnvelope(previewInstance, definition, accessProfile) with { Problems = problems };
            }

            // Declarative cross-field business rules (StageDefinition.Validations) — the
            // engine-native alternative to a host's ValidateAdvance override, checked once
            // field-level validation has already passed. Evaluated on the same merge of
            // persisted + just-submitted values FieldValueValidator above just accepted, never
            // on stale persisted data or on anything the client could claim was pre-checked.
            var stageValidationProblems = EvaluateStageValidations(instance, definition, currentStage, fieldValues, action);
            if (stageValidationProblems.Count > 0)
            {
                var previewInstance = instance with { FieldValues = Merge(instance.FieldValues, fieldValues) };
                return BuildEnvelope(previewInstance, definition, accessProfile) with { Problems = stageValidationProblems };
            }
        }

        if (ValidateAdvance(instance, definition, fieldValues) is { } validationEnvelope)
        {
            return validationEnvelope;
        }

        // Check if the target is a gateway rather than a plain stage.
        var nextGateway = FindGateway(definition, transition.ToState);
        if (nextGateway != null)
        {
            return string.Equals(nextGateway.GatewayType, "Split", StringComparison.Ordinal)
                ? HandleSplitGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile)
                : HandleJoinGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile);
        }

        // Regular stage transition (single- or multi-cursor).
        if (instance.Cursors.Count > 0)
        {
            // Multi-cursor: advance only the cursor currently at this stage.
            var sourceCursor = instance.Cursors.FirstOrDefault(c =>
                c.CurrentNodeKey == visibleWorkItem.StageKey && !c.IsAtGateway);
            var updatedCursors = MoveCursor(instance.Cursors, sourceCursor?.CursorId, transition.ToState, isAtGateway: false);
            var primaryStage = FirstActiveStageCursorKey(updatedCursors) ?? transition.ToState;
            var mergedMultiFieldValues = Merge(instance.FieldValues, fieldValues);
            var movedCursor = updatedCursors.FirstOrDefault(c => c.CursorId == sourceCursor?.CursorId);
            var newInvocations = movedCursor is not null
                ? ExecuteOnEnterSupportSystemActions(instanceId, definition, mergedMultiFieldValues, movedCursor)
                : [];
            var updatedMulti = instance with
            {
                CurrentStage = primaryStage,
                Cursors = updatedCursors,
                StateVersion = instance.StateVersion + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                FieldValues = mergedMultiFieldValues,
                SupportSystemInvocations = instance.SupportSystemInvocations.Concat(newInvocations).ToArray()
            };
            SaveInstance(updatedMulti);
            Logger.LogInformation(
                "Multi-cursor advance instance {Id}: cursor {CursorId} → {To}",
                instanceId, sourceCursor?.CursorId ?? "(none)", transition.ToState);
            return BuildEnvelope(updatedMulti, definition, accessProfile);
        }

        var updated = instance with
        {
            CurrentStage = transition.ToState,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = Merge(instance.FieldValues, fieldValues)
        };

        SaveInstance(updated);
        Logger.LogInformation(
            "Advanced instance {Id}: {From} → {To}",
            instanceId,
            visibleWorkItem.StageKey,
            transition.ToState);

        return BuildEnvelope(updated, definition, accessProfile);
    }

    public IEnumerable<ServiceRequest> GetAllInstances() => _instanceStore.GetAll();

    public ServiceRequestListEnvelope GetInstances(string tenantId, string userId)
    {
        var userInstances = _instanceStore.GetAll()
            .Where(i => string.Equals(i.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(i.UserId, userId, StringComparison.Ordinal))
            .Select(instance =>
            {
                _definitions.TryGetValue(instance.BlueprintKey, out var definition);
                var stage = definition?.Stages.FirstOrDefault(s => s.StageKey == instance.CurrentStage);
                var stepType = stage?.Components.InferStepType() ?? "question";

                return new ServiceRequestSummary
                {
                    InstanceId = instance.InstanceId,
                    BlueprintKey = instance.BlueprintKey,
                    BlueprintDisplayName = definition?.DisplayName ?? instance.BlueprintKey,
                    CurrentStateKey = instance.CurrentStage,
                    CurrentStateDisplayName = stage?.DisplayName ?? instance.CurrentStage,
                    StepType = stepType,
                    CreatedAt = instance.CreatedAt.DateTime,
                    LastUpdatedAt = instance.UpdatedAt.DateTime,
                    CanContinue = stepType != "confirmation",
                    IsCompleted = stepType == "confirmation",
                    ServiceRequestPageUrl = null,
                    RequestPolicy = definition?.RequestPolicy ?? "single"
                };
            })
            .ToList();

        return new ServiceRequestListEnvelope
        {
            Instances = userInstances
        };
    }

    public IReadOnlyList<string> ClaimInstances(string tenantId, string fromUserId, string toUserId)
    {
        if (string.IsNullOrWhiteSpace(fromUserId)
            || string.IsNullOrWhiteSpace(toUserId)
            || string.Equals(fromUserId, toUserId, StringComparison.Ordinal))
        {
            return [];
        }

        var allInstances = _instanceStore.GetAll().ToList();
        var blueprintsAlreadyOwned = allInstances
            .Where(i => string.Equals(i.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(i.UserId, toUserId, StringComparison.Ordinal))
            .Select(i => i.BlueprintKey)
            .ToHashSet(StringComparer.Ordinal);

        var claimed = new List<string>();
        foreach (var instance in allInstances)
        {
            if (!string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal)
                || !string.Equals(instance.UserId, fromUserId, StringComparison.Ordinal))
            {
                continue;
            }

            if (blueprintsAlreadyOwned.Contains(instance.BlueprintKey))
            {
                // The signed-in user already has their own instance of this blueprint — leave
                // the anonymous one behind rather than silently discarding whichever loses.
                continue;
            }

            _instanceStore.Save(instance with
            {
                UserId = toUserId,
                IsAuthenticated = true,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            claimed.Add(instance.InstanceId);
        }

        return claimed;
    }

    public QueueWorkListEnvelope GetQueueWorkItems(ActorProfile accessProfile)
    {
        var items = _instanceStore.GetAll()
            .SelectMany(instance =>
            {
                if (!_definitions.TryGetValue(instance.BlueprintKey, out var definition))
                {
                    return Array.Empty<QueueWorkItem>();
                }

                // A join-gateway item legitimately has no available actions — the actor is waiting
                // on another queue, not choosing anything. Filtering purely on "has actions" hid
                // those entirely, so an application sent to a support system vanished from the
                // caseworker's own queue with no way back to it (see QueueWorkItem.IsWaiting).
                return FindAccessibleWorkItems(instance, definition, accessProfile)
                    .Where(item => item.AvailableActions.Count > 0 || item.IsJoinGateway)
                    .Select(item => item.ToEnvelopeItem(instance, definition))
                    .ToArray();
            })
            .OrderBy(item => item.BlueprintDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StateDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();

        return new QueueWorkListEnvelope
        {
            Items = items
        };
    }

    public IEnumerable<ServiceBlueprint> GetAllDefinitions() => _definitions.Values;

    public ServiceBlueprint? GetDefinition(string key) =>
        _definitions.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>
    /// Registers or updates a definition in the live engine — an upsert, not update-only. A brand
    /// new key (one this engine has never seen, e.g. a blueprint an agent or human just authored
    /// from scratch via save_service_blueprint) must actually become servable here, or the documented
    /// promise that "a save reaches the live engine immediately" is false for exactly the scenario
    /// — authoring a new service — the whole toolkit exists for. Always returns true.
    /// </summary>
    public bool UpdateDefinition(string key, ServiceBlueprint updated)
    {
        var isNewKey = !_definitions.ContainsKey(key);
        _definitions[key] = updated;
        Logger.LogInformation(
            isNewKey ? "Service blueprint registered in-memory: {Key}" : "Service blueprint updated in-memory: {Key}",
            key);
        return true;
    }

    /// <summary>
    /// Removes a definition from the live engine — the delete-side counterpart to
    /// <see cref="UpdateDefinition"/>. Existing instances already running against this key are
    /// left untouched (they keep whatever stage they have; the definition lookups they depend on,
    /// e.g. in <see cref="GetCurrent(string,string,string,ActorProfile,string?,string?)"/>,
    /// will simply start failing with DEFINITION_NOT_FOUND) — deleting a service blueprint that
    /// still has active instances is a host-authoring concern to guard against, not this engine's.
    /// </summary>
    public bool RemoveDefinition(string key) => _definitions.Remove(key);

    public bool Reset(string instanceId)
    {
        if (!_instanceStore.Remove(instanceId))
        {
            return false;
        }

        Logger.LogInformation("Reset (deleted) instance {Id}", instanceId);
        return true;
    }

    public void ResetAll()
    {
        _instanceStore.Clear();
        Logger.LogInformation("ResetAll: all service requests cleared");
    }

    protected virtual ServiceRequestResponseEnvelope? ValidateAdvance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        Dictionary<string, object?>? fieldValues) => null;

    protected virtual ServiceRequestResponseEnvelope? InitializeNewInstance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        string? action) => null;

    /// <summary>
    /// Host hook invoked before a stage's components are rendered. Returns structured
    /// display data for the step (surfaced as <see cref="StepContent.Data"/> and resolved
    /// into "interactive" components via their DataKey), or null when the stage needs none.
    /// Implementations may enrich <paramref name="instance"/>.FieldValues (e.g. with freshly
    /// computed results) before rendering; the shared FieldValues dictionary makes such
    /// enrichment visible to the stored instance.
    /// </summary>
    protected virtual System.Text.Json.Nodes.JsonObject? BuildRenderData(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage) => null;

    /// <summary>
    /// Host hook supplying typed values for the definition's <c>source: "service"</c>
    /// calculation fields (e.g. a member record from a system of record). Values may be
    /// scalars (decimal/bool/string) or nested string-keyed dictionaries for dotted access.
    /// A subclassing host overrides this method directly; a composed caller (e.g. the
    /// simulation runner) supplies the constructor's <c>serviceInputsResolver</c> delegate
    /// instead — this default implementation prefers that delegate when one was given.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, object?>? ResolveServiceInputs(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage) => _serviceInputsResolver?.Invoke(instance, definition, stage);

    protected bool TryGetInstance(string instanceId, out ServiceRequest instance) =>
        _instanceStore.TryGet(instanceId, out instance!);

    protected void SaveInstance(ServiceRequest instance) =>
        _instanceStore.Save(instance);

    /// <summary>
    /// The most recently computed <see cref="CalculationResult"/> for an instance, if its
    /// current stage has a calculations block and it evaluated cleanly — <c>null</c> if the
    /// instance doesn't exist, its stage has no calculations block, or evaluation failed. A
    /// composed caller (e.g. the simulation runner) uses this to read raw calculated values
    /// without duplicating evaluation itself.
    /// </summary>
    public CalculationResult? GetLastCalculationResult(string instanceId) =>
        TryGetInstance(instanceId, out var instance) ? instance.LastCalculationResult : null;

    protected ServiceRequestResponseEnvelope BuildEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile)
    {
        var workItems = FindAccessibleWorkItems(instance, definition, accessProfile);
        var visibleItem = workItems.FirstOrDefault();

        if (visibleItem is null)
        {
            return ErrorEnvelope(
                "Access denied to the current queue.",
                "ACCESS_DENIED");
        }

        if (visibleItem.IsJoinGateway)
        {
            var joinGateway = FindGateway(definition, visibleItem.StageKey);
            if (joinGateway is not null)
            {
                // A join gateway is exactly where a caseworker's own cursor sits waiting on an
                // automation-queue cursor that's itself waiting on a support-system call — the
                // same "waiting behind the line of visibility" screen citizen/caseworker joins
                // already use. Before rendering that wait screen again, give any still-pending
                // support-system invocation blocking THIS gateway a chance to resolve via poll —
                // the generic, always-on counterpart to the webhook receiver resolving one
                // asynchronously. If anything resolved, its own Advance() call already saved
                // fresh state (and possibly released the join outright); re-derive the response
                // from that fresh state rather than the now-stale `instance` this method started
                // with.
                if (TryPollResolveSupportSystemInvocations(instance, definition, joinGateway)
                    && TryGetInstance(instance.InstanceId, out var refreshed))
                {
                    return BuildEnvelope(refreshed, definition, accessProfile);
                }

                return BuildJoinWaitingEnvelope(instance, definition, joinGateway);
            }
        }

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == visibleItem.StageKey);
        if (stage == null)
        {
            return ErrorEnvelope(
                $"State '{visibleItem.StageKey}' not found in definition '{definition.DefinitionKey}'.",
                "STATE_NOT_FOUND");
        }

        var renderData = BuildRenderData(instance, definition, stage);
        var calc = EvaluateDefinitionCalculations(instance, definition, stage);
        if (calc is not null)
        {
            renderData ??= new JsonObject();
            renderData["live"] = BuildLiveModel(definition, calc);
        }

        var components = BuildComponents(stage.Components, instance.FieldValues, calc);
        var effectiveStepType = stage.Components.InferStepType();
        var waitingComponent = stage.Components.OfType<WaitingComponent>().FirstOrDefault();

        var render = new StepContent
        {
            StepType = effectiveStepType,
            StateDisplayName = stage.DisplayName,
            Components = components,
            AvailableActions = visibleItem.AvailableActions.ToArray(),
            Data = renderData
        };

        var responseState = effectiveStepType switch
        {
            "status-timeline" => "defer",
            "confirmation" => "complete",
            _ => "render"
        };

        return new ServiceRequestResponseEnvelope
        {
            InstanceId = instance.InstanceId,
            ResponseState = responseState,
            StateVersion = instance.StateVersion,
            CorrelationId = instance.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            PollAfterMs = waitingComponent?.PollIntervalMs,
            Render = render,
            RequestPolicy = definition.RequestPolicy
        };
    }

    protected bool CanAccessInstance(
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

    protected bool CanStartInitialState(ServiceBlueprint definition, ActorProfile accessProfile)
    {
        var initialStage = definition.Stages.FirstOrDefault(stage =>
            string.Equals(stage.StageKey, definition.InitialStage, StringComparison.Ordinal));

        var queueName = initialStage is null ? null : ResolveQueueName(definition, initialStage);
        return accessProfile.CanViewQueue(queueName) && accessProfile.CanStartQueue(queueName);
    }

    protected IReadOnlyList<AccessibleWorkItem> FindAccessibleWorkItems(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile)
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
                    items.Add(new AccessibleWorkItem(
                        stage.StageKey,
                        stage.DisplayName,
                        queueName,
                        IsJoinGateway: false,
                        BuildAvailableActions(instance, definition, stage.StageKey, queueName, accessProfile)));
                }
            }

            return items;
        }

        foreach (var cursor in instance.Cursors.Where(candidate => !candidate.IsAtGateway))
        {
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

            items.Add(new AccessibleWorkItem(
                stage.StageKey,
                stage.DisplayName,
                queueName,
                IsJoinGateway: false,
                BuildAvailableActions(instance, definition, stage.StageKey, queueName, accessProfile)));
        }

        foreach (var cursor in instance.Cursors.Where(candidate => candidate.IsAtGateway))
        {
            var gateway = FindGateway(definition, cursor.CurrentNodeKey);
            if (gateway is null || !string.Equals(gateway.GatewayType, "Join", StringComparison.Ordinal))
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
                IsJoinGateway: true,
                []));
        }

        return items
            .OrderByDescending(item => string.Equals(item.StageKey, instance.CurrentStage, StringComparison.Ordinal))
            .ThenBy(item => item.IsJoinGateway)
            .ThenBy(item => item.StageKey, StringComparer.Ordinal)
            .ToArray();
    }

    protected bool CanViewQueue(
        ServiceBlueprint definition,
        string? queueKey,
        string? queueName,
        ActorProfile accessProfile)
    {
        return accessProfile.CanViewQueue(queueName);
    }

    protected IReadOnlyList<ServiceRequestAction> BuildAvailableActions(
        ServiceRequest instance,
        ServiceBlueprint definition,
        string stageKey,
        string? queueName,
        ActorProfile accessProfile)
    {
        var transitions = GetOutgoingTransitions(definition, stageKey);

        if (string.IsNullOrWhiteSpace(queueName))
        {
            transitions = transitions.Where(transition => transition.RequiresRole is null).ToArray();
        }
        else if (!accessProfile.CanActInQueue(queueName))
        {
            return [];
        }

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
                var scope = BuildCalculationScope(instance, definition, stage, pendingFieldValues: null);
                transitions = transitions
                    .Where(transition => EvaluateShowWhen(transition.ShowWhen, scope, definition.Calculations))
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

    /// <summary>
    /// A route's trigger can be authored blank (an AI agent leaving it empty rather than omitting
    /// it, so it survives as "" not null) — default it to "continue" here, the single place raw
    /// route.Trigger values are read into a transition's Action, so the rendered button's value
    /// and the action-matching in <see cref="Advance"/> always agree on the same non-empty key.
    /// </summary>
    private static string ResolveTrigger(string? trigger) =>
        string.IsNullOrWhiteSpace(trigger) ? "continue" : trigger;

    protected static string? ResolveQueueName(ServiceBlueprint definition, string? queueKey)
    {
        if (string.IsNullOrWhiteSpace(queueKey))
        {
            return null;
        }

        var queue = GetQueues(definition).FirstOrDefault(candidate =>
            string.Equals(candidate.Key, queueKey, StringComparison.Ordinal));

        return queue?.Key ?? queueKey;
    }

    protected static string? ResolveQueueName(ServiceBlueprint definition, StageDefinition? stage) =>
        stage is null
            ? null
            : ResolveQueueName(definition, GetQueueKey(stage));

    protected static string? ResolveQueueName(ServiceBlueprint definition, ServiceBlueprintGatewayDefinition? gateway) =>
        gateway is null
            ? null
            : ResolveQueueName(definition, gateway.QueueKey);

    protected static string? GetQueueKey(StageDefinition? stage) =>
        FirstNonEmpty(stage?.QueueKey, stage?.Metadata?.QueueKey);

    protected static IReadOnlyList<QueueDefinition> GetQueues(ServiceBlueprint definition) =>
        definition.Queues ?? [];

    protected static IReadOnlyList<ServiceBlueprintGatewayDefinition> GetGateways(ServiceBlueprint definition) =>
        definition.Gateways ?? definition.Metadata?.Gateways ?? [];

    protected static IReadOnlyList<RouteFile> GetOutgoingTransitions(
        ServiceBlueprint definition,
        string sourceKey)
    {
        var stage = definition.Stages.FirstOrDefault(candidate =>
            string.Equals(candidate.StageKey, sourceKey, StringComparison.Ordinal));
        if (stage?.Routes is { Count: > 0 })
        {
            return stage.Routes
                .Select(route => new RouteFile
                {
                    FromState = sourceKey,
                    ToState = route.Target,
                    Action = ResolveTrigger(route.Trigger),
                    Label = route.Label,
                    Style = route.Style,
                    RequiresRole = route.RequiresRole,
                    ShowWhen = route.ShowWhen,
                    Actions = route.Actions
                })
                .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
                .ThenBy(transition => transition.Action, StringComparer.Ordinal)
                .ToArray();
        }

        var gateway = FindGateway(definition, sourceKey);
        if (gateway?.Routes is { Count: > 0 })
        {
            return gateway.Routes
                .Select(route => new RouteFile
                {
                    FromState = gateway.Key,
                    ToState = route.Target,
                    Action = ResolveTrigger(route.Trigger),
                    Label = route.Label,
                    Style = route.Style,
                    RequiresRole = route.RequiresRole,
                    ShowWhen = route.ShowWhen,
                    Actions = route.Actions
                })
                .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
                .ThenBy(transition => transition.Action, StringComparer.Ordinal)
                .ToArray();
        }

        var sourceGateway = GetGateways(definition)
            .Where(candidate =>
                string.Equals(candidate.Source, sourceKey, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ToArray();

        if (sourceGateway.Length == 0)
        {
            return [];
        }

        var transitions = new List<RouteFile>();

        foreach (var candidate in sourceGateway)
        {
            var routes = candidate.Routes ?? [];
            var distinctTriggers = routes
                .Select(route => route.Trigger)
                .Where(trigger => !string.IsNullOrWhiteSpace(trigger))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var isParallelFork = string.Equals(candidate.GatewayType, "Split", StringComparison.OrdinalIgnoreCase)
                                 && routes.Count >= 2
                                 && distinctTriggers.Length == 1;

            if (isParallelFork)
            {
                transitions.Add(new RouteFile
                {
                    FromState = sourceKey,
                    ToState = candidate.Key,
                    Action = distinctTriggers[0]
                });

                continue;
            }

            transitions.AddRange(routes.Select(route => new RouteFile
            {
                FromState = sourceKey,
                ToState = route.Target,
                Action = ResolveTrigger(route.Trigger),
                Label = route.Label,
                Style = route.Style,
                RequiresRole = route.RequiresRole,
                ShowWhen = route.ShowWhen,
                Actions = route.Actions
            }));
        }

        return transitions
            .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
            .ThenBy(transition => transition.Action, StringComparer.Ordinal)
            .ToArray();
    }


    protected sealed record AccessibleWorkItem(
        string StageKey,
        string DisplayName,
        string? QueueName,
        bool IsJoinGateway,
        IReadOnlyList<ServiceRequestAction> AvailableActions)
    {
        public QueueWorkItem ToEnvelopeItem(
            ServiceRequest instance,
            ServiceBlueprint definition) =>
            new()
            {
                InstanceId = instance.InstanceId,
                BlueprintKey = instance.BlueprintKey,
                BlueprintDisplayName = definition.DisplayName,
                StageKey = StageKey,
                StateDisplayName = DisplayName,
                QueueName = QueueName,
                TenantId = instance.TenantId,
                UserId = instance.UserId,
                StateVersion = instance.StateVersion,
                AvailableActions = AvailableActions,
                IsWaiting = IsJoinGateway
            };
    }

    protected static ServiceRequestResponseEnvelope ErrorEnvelope(string message, string code) =>
        new()
        {
            InstanceId = string.Empty,
            ResponseState = "error",
            StateVersion = 0,
            CorrelationId = Guid.NewGuid().ToString(),
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Problems = [new ServiceRequestProblem { FieldKey = string.Empty, Message = message, Code = code }]
        };

    private ServiceRequestResponseEnvelope CreateAndRegisterNewInstance(
        string blueprintKey,
        string tenantId,
        string userId,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string? action,
        string logMessage,
        params object?[] additionalLogArgs)
    {
        var instance = CreateNewInstance(
            blueprintKey, tenantId, userId, definition.InitialStage, ResolveIsAuthenticated(tenantId, userId));
        if (InitializeNewInstance(instance, definition, action) is { } error)
        {
            return error;
        }

        _instanceStore.Save(instance);

        Logger.LogInformation(logMessage, [instance.InstanceId, .. additionalLogArgs]);
        return BuildEnvelope(instance, definition, accessProfile);
    }

    private static ServiceRequest CreateNewInstance(
        string blueprintKey,
        string tenantId,
        string userId,
        string initialStage,
        bool isAuthenticated)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServiceRequest
        {
            InstanceId = Guid.NewGuid().ToString(),
            BlueprintKey = blueprintKey,
            TenantId = tenantId,
            UserId = userId,
            IsAuthenticated = isAuthenticated,
            CurrentStage = initialStage,
            StateVersion = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Whether <paramref name="userId"/> identifies a signed-in user, for a store that wants
    /// to apply a different retention policy for authenticated vs anonymous instances (see
    /// <see cref="ServiceRequest.IsAuthenticated"/>). The base engine has no identity
    /// model of its own — always false — a host overrides this using whatever identity
    /// resolution its own request pipeline already performs.
    /// </summary>
    protected virtual bool ResolveIsAuthenticated(string tenantId, string userId) => false;

    /// <summary>Evaluated calculation stage for one render pass.</summary>
    protected sealed record CalculationRenderContext(
        ServiceBlueprintCalculationSet Set,
        IReadOnlyDictionary<string, object?> Scope,
        CalculationResult Result,
        IReadOnlyDictionary<string, object?> DisplayValues);

    private readonly CalculationEvaluator _calculationEvaluator = new();

    private CalculationRenderContext? EvaluateDefinitionCalculations(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage)
    {
        if (definition.Calculations is null)
        {
            return null;
        }

        try
        {
            var serviceInputs = ResolveServiceInputs(instance, definition, stage);
            var scope = CalculationScopeBuilder.Build(definition, instance.FieldValues, serviceInputs);
            var result = _calculationEvaluator.Evaluate(definition.Calculations, scope);

            // Full scope (inputs + calculated fields) for showWhen evaluation.
            var fullScope = new Dictionary<string, object?>(scope, StringComparer.Ordinal);
            foreach (var (name, value) in result.Fields)
            {
                fullScope[name] = value;
            }

            // Display overlay: saved values, then calculated fields formatted per their
            // declared format — this is what stat-groups and summary-lists resolve from.
            var display = new Dictionary<string, object?>(instance.FieldValues, StringComparer.Ordinal);
            foreach (var (name, value) in result.Fields)
            {
                var format = definition.Calculations.Fields.TryGetValue(name, out var field) ? field.Format : null;
                display[name] = FormatCalculatedValue(value, format);
            }

            // Last computed result is kept on the instance so a composed caller (e.g. the
            // simulation runner, which builds this engine rather than subclassing it) can
            // read raw calculated values without duplicating evaluation itself.
            SaveInstance(instance with { LastCalculationResult = result });

            return new CalculationRenderContext(definition.Calculations, fullScope, result, display);
        }
        catch (CalculationException exception)
        {
            Logger.LogWarning(
                exception,
                "Calculation evaluation failed for blueprint {Key}, stage {State}; rendering without calculated values.",
                definition.DefinitionKey,
                stage.StageKey);
            return null;
        }
    }

    private static string? FormatCalculatedValue(object? value, string? format) => value switch
    {
        null => null,
        decimal d when string.Equals(format, "gbp", StringComparison.OrdinalIgnoreCase) =>
            string.Create(
                System.Globalization.CultureInfo.GetCultureInfo("en-GB"),
                $"£{Math.Round(d, 0, MidpointRounding.AwayFromZero):N0}"),
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString()
    };

    private JsonObject BuildLiveModel(ServiceBlueprint definition, CalculationRenderContext calc)
    {
        var inputTypes = new JsonObject();
        var defaults = new JsonObject();
        foreach (var (fieldKey, (type, defaultValue)) in CalculationScopeBuilder.DescribeInputs(definition))
        {
            inputTypes[fieldKey] = type;
            if (defaultValue is not null)
            {
                defaults[fieldKey] = defaultValue;
            }
        }

        var serviceValues = new JsonObject();
        foreach (var (name, field) in calc.Set.Fields)
        {
            if (string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase)
                && calc.Scope.TryGetValue(name, out var value))
            {
                serviceValues[name] = ScopeValueToJson(value);
            }
        }

        return new JsonObject
        {
            ["calculations"] = JsonSerializer.SerializeToNode(calc.Set, LiveModelJsonOptions),
            ["inputTypes"] = inputTypes,
            ["defaults"] = defaults,
            ["service"] = serviceValues
        };
    }

    private static readonly JsonSerializerOptions LiveModelJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonNode? ScopeValueToJson(object? value) => value switch
    {
        null => null,
        decimal d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        string text => JsonValue.Create(text),
        IReadOnlyDictionary<string, object?> map => new JsonObject(
            map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ScopeValueToJson(pair.Value)))),
        _ => JsonValue.Create(value.ToString())
    };

    /// <summary>
    /// Shared by <see cref="Components.Component.ShowWhen"/> (component visibility) and
    /// <see cref="ServiceBlueprintRouteDefinition.ShowWhen"/> (route/action availability) — takes
    /// a raw scope rather than a <see cref="CalculationRenderContext"/> so a caller with no other
    /// use for the fuller context (route gating doesn't need <c>Result</c>/<c>Display</c>) isn't
    /// forced to build one just to call this.
    /// </summary>
    private bool EvaluateShowWhen(
        string? showWhen,
        IReadOnlyDictionary<string, object?>? scope,
        ServiceBlueprintCalculationSet? calculations)
    {
        if (string.IsNullOrWhiteSpace(showWhen) || scope is null)
        {
            return true;
        }

        try
        {
            return _calculationEvaluator.EvaluateExpression(showWhen, scope, calculations) is not false;
        }
        catch (CalculationException exception)
        {
            Logger.LogWarning(exception, "showWhen expression '{Expr}' failed; stays visible.", showWhen);
            return true;
        }
    }

    /// <summary>
    /// The scope a <c>showWhen</c>/stage-validation expression evaluates against: declared inputs
    /// plus calculated fields, exactly what <see cref="EvaluateDefinitionCalculations"/> also
    /// builds — but without that method's side effect of persisting
    /// <see cref="ServiceRequest.LastCalculationResult"/>, so it's safe to call from a hot,
    /// no-writes path like <see cref="BuildAvailableActions"/> (once per stage per queue render)
    /// without multiplying instance-store writes.
    /// </summary>
    /// <param name="pendingFieldValues">
    /// A submission in progress, merged over <paramref name="instance"/>'s already-persisted
    /// values before evaluating — <see langword="null"/> to evaluate against persisted state
    /// alone (what a caller deciding what to *render*, rather than validating a submission,
    /// wants).
    /// </param>
    private Dictionary<string, object?> BuildCalculationScope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage,
        Dictionary<string, object?>? pendingFieldValues)
    {
        var serviceInputs = ResolveServiceInputs(instance, definition, stage);
        var mergedFieldValues = pendingFieldValues is null
            ? instance.FieldValues
            : Merge(instance.FieldValues, pendingFieldValues);
        var baseScope = CalculationScopeBuilder.Build(definition, mergedFieldValues, serviceInputs);

        if (definition.Calculations is null)
        {
            return baseScope;
        }

        var evaluation = _calculationEvaluator.EvaluateCollectingErrors(definition.Calculations, baseScope);
        var fullScope = new Dictionary<string, object?>(baseScope, StringComparer.Ordinal);
        foreach (var (name, value) in evaluation.Result.Fields)
        {
            fullScope[name] = value;
        }
        return fullScope;
    }

    /// <summary>
    /// Evaluates <paramref name="stage"/>'s declarative <see cref="StageDefinition.Validations"/>
    /// against the merge of persisted + just-submitted field values — the same trust boundary
    /// <see cref="Advance(string, string, string, ActorProfile, string, int, Dictionary{string, object?})"/>
    /// already applies to field-level validation: never the stale persisted instance alone, never
    /// anything the client could claim was pre-validated.
    ///
    /// Failure is deliberately biased toward blocking, not toward permissiveness, unlike
    /// <see cref="EvaluateShowWhen"/>'s "stays visible" default: a <c>when</c> that doesn't
    /// evaluate to exactly <c>false</c> is treated as applying (ambiguous → check it), a
    /// <c>rule</c> that doesn't evaluate to exactly <c>true</c> is treated as failed (ambiguous →
    /// block), and a rule whose expressions throw is treated as failed rather than skipped — this
    /// is a hard gate, not a display hint, so an expression this engine can't confirm holds must
    /// never silently let a submission through. This should be rare in practice:
    /// <c>ServiceBlueprintAuthoringService.Validate</c> already statically checks every
    /// <c>when</c>/<c>rule</c> expression before a blueprint can be saved. A calculated field that
    /// fails only affects the specific rules that actually reference it (via
    /// <see cref="CalculationEvaluator.EvaluateCollectingErrors"/>), not every validation on the
    /// stage.
    /// </summary>
    private IReadOnlyList<ServiceRequestProblem> EvaluateStageValidations(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage,
        Dictionary<string, object?>? fieldValues,
        string action)
    {
        // A rule naming no actions guards every way out of the stage (the default, and what a
        // data-completeness rule wants). A rule naming actions guards only those — see
        // ServiceBlueprintStageValidationRule.Actions for why a stage with genuinely different
        // exits needs this to be expressible at all.
        var rules = (stage.Validations ?? [])
            .Where(rule => rule.Actions is not { Count: > 0 } scoped
                || scoped.Contains(action, StringComparer.Ordinal))
            .ToArray();

        if (rules.Length == 0)
        {
            return [];
        }

        var scope = BuildCalculationScope(instance, definition, stage, fieldValues);

        var problems = new List<ServiceRequestProblem>();
        foreach (var rule in rules)
        {
            bool failed;
            try
            {
                var applies = string.IsNullOrWhiteSpace(rule.When)
                    || _calculationEvaluator.EvaluateExpression(rule.When, scope, definition.Calculations) is not false;
                failed = applies
                    && _calculationEvaluator.EvaluateExpression(rule.Rule, scope, definition.Calculations) is not true;
            }
            catch (CalculationException exception)
            {
                Logger.LogWarning(
                    exception,
                    "Stage validation rule '{Code}' failed to evaluate for blueprint {Key}, stage {State}; treating as failed.",
                    rule.Code,
                    definition.DefinitionKey,
                    stage.StageKey);
                failed = true;
            }

            if (failed)
            {
                problems.Add(new ServiceRequestProblem
                {
                    FieldKey = rule.Field ?? stage.StageKey,
                    Message = rule.Message,
                    Code = rule.Code
                });
            }
        }

        return problems;
    }

    private ComponentRenderPayload[] BuildComponents(
        IReadOnlyList<Component> componentDefinitions,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        // Stat-groups and summary-lists resolve display values from the calculation
        // overlay when one exists; plain input values come from the instance as before.
        var displayValues = calc is null
            ? savedValues
            : new Dictionary<string, object?>(calc.DisplayValues, StringComparer.Ordinal);

        var result = new List<ComponentRenderPayload>();

        foreach (var component in componentDefinitions)
        {
            var payloadsBefore = result.Count;
            switch (component)
            {
                case FieldsetComponent fieldset:
                {
                    var fields = BuildFields(fieldset.Children, displayValues, calc);
                    if (fields.Length == 0)
                    {
                        Logger.LogWarning("Fieldset component contains no renderable fields");
                        continue;
                    }

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "fieldset",
                        Legend = fieldset.Legend,
                        LegendSize = fieldset.LegendSize,
                        Fields = fields
                    });
                    break;
                }

                case SummaryListComponent summary:
                {
                    // A summary-list echoes values already collected (and validated) on the
                    // stages its "change" links point back to — never a fresh submission on
                    // whatever stage/transition happens to render it (e.g. a check-answers
                    // page's own "submit"). ReadOnly = true keeps FieldValueValidator from
                    // demanding these be resubmitted alongside that stage's own real fields.
                    var fields = BuildFields(summary.Children, displayValues, calc)
                        .Select(f => f with { ReadOnly = true })
                        .ToArray();
                    if (fields.Length == 0)
                    {
                        Logger.LogWarning("Summary-list component contains no renderable fields");
                        continue;
                    }

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "summary-list",
                        Title = summary.Title,
                        SourceStateKey = summary.ChangeStateKey,
                        Fields = fields
                    });
                    break;
                }

                case AccordionComponent accordion:
                {
                    var sections = accordion.Sections
                        .Select(section => new AccordionSectionPayload
                        {
                            Heading = section.Heading,
                            Summary = section.Summary,
                            Fields = BuildFields(section.Children, displayValues, calc)
                        })
                        .ToArray();

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "accordion",
                        AccordionSections = sections
                    });
                    break;
                }

                case WaitingComponent waiting:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "waiting",
                        Content = _sanitizer.Sanitize(waiting.Content),
                        ExpectedWaitSeconds = waiting.ExpectedWaitSeconds,
                        PollIntervalMs = waiting.PollIntervalMs,
                        AllowDefer = waiting.AllowDefer,
                        DeferMessage = waiting.DeferMessage
                    });
                    break;

                case PanelComponent panel:
                    result.Add(new ComponentRenderPayload { Type = "panel", Heading = panel.Heading });
                    break;

                case BodyComponent body:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "body",
                        Content = _sanitizer.Sanitize(body.Content)
                    });
                    break;

                case HeadingComponent heading:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "heading",
                        Content = heading.Content,
                        Level = heading.Level
                    });
                    break;

                case InsetTextComponent inset:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "inset-text",
                        Content = _sanitizer.Sanitize(inset.Content)
                    });
                    break;

                case WarningTextComponent warning:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "warning-text",
                        Content = _sanitizer.Sanitize(warning.Content)
                    });
                    break;

                case DetailsComponent details:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "details",
                        Heading = details.Heading,
                        Content = _sanitizer.Sanitize(details.Content)
                    });
                    break;

                case NotificationBannerComponent banner:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "notification-banner",
                        Heading = banner.Heading,
                        Content = _sanitizer.Sanitize(banner.Content),
                        BannerType = banner.BannerType
                    });
                    break;

                case TaskListComponent taskList:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "task-list",
                        TaskSections = taskList.Sections?.Select(section => new TaskSectionPayload
                        {
                            Heading = section.Heading,
                            Tasks = section.Tasks.Select(task => new TaskItemPayload
                            {
                                Label = task.Label,
                                Href = task.Href ?? task.StageKey,
                                Status = "not-started"
                            }).ToArray()
                        }).ToArray()
                    });
                    break;

                case StatGroupComponent statGroup:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "stat-group",
                        Title = statGroup.Title,
                        Stats = statGroup.Items.Select(item => new StatItem
                        {
                            Label = item.Label,
                            FieldKey = item.FieldKey,
                            Value = displayValues.TryGetValue(item.FieldKey, out var statValue)
                                ? statValue?.ToString()
                                : null,
                            Qualifier = item.Qualifier,
                            Emphasis = item.Emphasis
                        }).ToArray()
                    });
                    break;

                case ChartComponent chart:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "chart",
                        Heading = chart.Title,
                        ChartJson = BuildChartJson(chart, calc)
                    });
                    break;

                case BulkDataReviewComponent bulkReview:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "bulk-data-review",
                        Title = bulkReview.Title,
                        DatasetId = displayValues.TryGetValue(bulkReview.DatasetIdField, out var datasetIdValue)
                            ? datasetIdValue?.ToString()
                            : null,
                        PageSize = bulkReview.PageSize,
                    });
                    break;

                case InputComponent input:
                {
                    var fields = BuildFields(new[] { (Component)input }, displayValues, calc);
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "fieldset",
                        Fields = fields
                    });
                    break;
                }
            }

            if (component.ShowWhen is { Length: > 0 } showWhen)
            {
                var visible = EvaluateShowWhen(showWhen, calc?.Scope, calc?.Set);
                for (var i = payloadsBefore; i < result.Count; i++)
                {
                    result[i] = result[i] with { ShowWhen = showWhen, Hidden = !visible };
                }
            }
        }

        return result.ToArray();
    }

    private static string BuildChartJson(ChartComponent chart, CalculationRenderContext? calc)
    {
        var bands = new JsonArray();
        foreach (var band in chart.Bands)
        {
            bands.Add(new JsonObject
            {
                ["key"] = band.Key,
                ["label"] = band.Label,
                ["color"] = band.Color
            });
        }

        var rows = new JsonArray();
        if (calc is not null && calc.Result.Series.TryGetValue(chart.Series, out var seriesRows))
        {
            foreach (var seriesRow in seriesRows)
            {
                var row = new JsonObject();
                foreach (var (column, value) in seriesRow)
                {
                    row[column] = ScopeValueToJson(value);
                }

                rows.Add(row);
            }
        }

        return new JsonObject
        {
            ["kind"] = chart.Kind,
            ["x"] = chart.X,
            ["xLabelEvery"] = chart.XLabelEvery,
            ["series"] = chart.Series,
            ["bands"] = bands,
            ["rows"] = rows
        }.ToJsonString();
    }

    private static FieldRenderPayload[] BuildFields(
        IEnumerable<Component> children,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        var fields = new List<FieldRenderPayload>();

        foreach (var child in children)
        {
            switch (child)
            {
                case InputComponent input:
                    fields.Add(BuildInputPayload(input, savedValues, calc));

                    var conditional = (child as RadiosComponent)?.ConditionalChildren
                                      ?? (child as CheckboxesComponent)?.ConditionalChildren;
                    if (conditional != null)
                    {
                        foreach (var (optionValue, subComponents) in conditional)
                        {
                            foreach (var sub in subComponents.GetAllInputs())
                            {
                                fields.Add(BuildInputPayload(sub, savedValues, calc) with
                                {
                                    ConditionalOn = input.FieldKey,
                                    VisibleWhen = optionValue
                                });
                            }
                        }
                    }

                    break;

                case FieldsetComponent nestedFieldset:
                    fields.AddRange(BuildFields(nestedFieldset.Children, savedValues, calc));
                    break;
            }
        }

        return fields.ToArray();
    }

    private static FieldRenderPayload BuildInputPayload(
        InputComponent input,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        var fieldType = InputFieldType(input);
        return new FieldRenderPayload
        {
            FieldKey = input.FieldKey,
            Label = input.Label,
            Hint = input.Hint,
            FieldType = fieldType,
            Required = input.Required,
            Options = input switch
            {
                SelectComponent select => select.Options,
                RadiosComponent radios => radios.Options,
                CheckboxesComponent checkboxes => checkboxes.Options,
                GuidanceChecklistComponent guidance => guidance.Items.Select(i => i.Key).ToList(),
                _ => null
            },
            Value = GetDisplayValue(input, fieldType, savedValues) ?? ResolveDefaultFrom(input, calc) ?? input.Default,
            MinLength = input switch
            {
                TextInputComponent text => text.MinLength,
                TextareaComponent textarea => textarea.MinLength,
                _ => null
            },
            MaxLength = input switch
            {
                TextInputComponent text => text.MaxLength,
                TextareaComponent textarea => textarea.MaxLength,
                _ => null
            },
            Pattern = input switch
            {
                TextInputComponent text => text.Pattern,
                EmailComponent email => email.Pattern,
                _ => null
            },
            Min = input switch
            {
                NumberInputComponent number => number.Min,
                DecimalInputComponent decimalInput => decimalInput.Min,
                SliderComponent slider => slider.Min,
                _ => null
            },
            Max = input switch
            {
                NumberInputComponent number => number.Max,
                DecimalInputComponent decimalInput => decimalInput.Max,
                SliderComponent slider => slider.Max,
                _ => null
            },
            Step = input switch
            {
                SliderComponent slider => slider.Step,
                _ => null
            },
            Suffix = input switch
            {
                SliderComponent slider => slider.Suffix,
                _ => null
            },
            Prefix = input switch
            {
                TextInputComponent text => text.Prefix,
                NumberInputComponent number => number.Prefix,
                DecimalInputComponent decimalInput => decimalInput.Prefix,
                SliderComponent slider => slider.Prefix,
                _ => null
            },
            ConditionalOn = input.ConditionalOn,
            VisibleWhen = input.VisibleWhen,
            ChangeStateKey = input.ChangeStateKey,
            AcceptedFileTypes = input switch
            {
                FileUploadComponent file => file.AcceptedFileTypes,
                _ => null
            },
            MaxSizeBytes = input switch
            {
                FileUploadComponent file => file.MaxSizeBytes,
                _ => null
            },
            GuidanceItems = input switch
            {
                GuidanceChecklistComponent guidance => guidance.Items,
                _ => null
            }
        };
    }

    /// <summary>
    /// The <see cref="FieldRenderPayload.FieldType"/> a host's <c>GovUkComponentRenderer</c>
    /// dispatches rendering on for this input — its registered discriminator (e.g.
    /// <c>"text"</c>, <c>"radio"</c>), from <see cref="ComponentTypeRegistry"/>. Was previously a
    /// hand-written switch over the built-in CLR types (kept exactly in sync with the registry's
    /// own discriminators by luck, not by construction) — meaning a third-party InputComponent
    /// subtype fell through to its <c>_ =&gt; "text"</c> fallback and could never reach a
    /// <c>RegisterField</c> override registered under its own type name, quietly breaking the
    /// extensibility this registry exists to provide.
    /// </summary>
    private static string InputFieldType(InputComponent input) => ComponentTypeRegistry.DiscriminatorFor(input);

    private static object? GetDisplayValue(
        InputComponent input,
        string fieldType,
        Dictionary<string, object?> savedValues)
    {
        var raw = savedValues.TryGetValue(input.FieldKey, out var value) ? value : null;
        if (raw == null)
        {
            return null;
        }

        if (fieldType == "checkboxlist" || fieldType == "checkboxes")
        {
            var rawString = raw switch
            {
                string stringValue => stringValue,
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
                _ => null
            };

            if (rawString != null)
            {
                raw = string.Join(
                    ", ",
                    rawString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        if (fieldType == "file-upload")
        {
            // A freshly-uploaded file survives as its original CLR type for the rest of the
            // current request; a previously-uploaded one reloads from persistence as a boxed
            // JsonElement (no custom converter on FieldValues) — display the original filename
            // either way, never the reference object itself.
            return raw switch
            {
                ServiceRequestFileReference reference => reference.OriginalFileName,
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object
                    && jsonElement.TryGetProperty(nameof(ServiceRequestFileReference.OriginalFileName), out var nameProperty)
                    => nameProperty.GetString(),
                _ => null
            };
        }

        var prefix = input switch
        {
            TextInputComponent text => text.Prefix,
            NumberInputComponent number => number.Prefix,
            DecimalInputComponent decimalInput => decimalInput.Prefix,
            _ => null
        };

        return !string.IsNullOrEmpty(prefix)
            ? $"{prefix}{raw}"
            : raw;
    }

    /// <summary>
    /// Resolves <see cref="InputComponent.DefaultFrom"/> against the calculation display
    /// overlay — the same already-formatted scope stat-groups and summary-lists read from, so
    /// a "£20"-style gbp format applies here too if the named field declares one. Only ever
    /// called when there's no saved value yet (see <see cref="BuildInputPayload"/>'s value
    /// chain), so a visitor's own submitted choice always overrides this — it's a default, not
    /// a lock.
    /// </summary>
    private static string? ResolveDefaultFrom(InputComponent input, CalculationRenderContext? calc)
    {
        if (string.IsNullOrWhiteSpace(input.DefaultFrom) || calc is null)
        {
            return null;
        }

        return calc.DisplayValues.TryGetValue(input.DefaultFrom, out var value)
            ? value?.ToString()
            : null;
    }

    // ─── Gateway helpers ──────────────────────────────────────────────────────

    protected static ServiceBlueprintGatewayDefinition? FindGateway(ServiceBlueprint definition, string nodeKey) =>
        GetGateways(definition).FirstOrDefault(g =>
            string.Equals(g.Key, nodeKey, StringComparison.Ordinal));

    protected ServiceRequestResponseEnvelope HandleSplitGatewayAdvance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        RouteFile arrivingTransition,
        ServiceBlueprintGatewayDefinition splitGateway,
        Dictionary<string, object?>? fieldValues,
        ActorProfile accessProfile)
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
            return ErrorEnvelope(
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
                string.Equals(targetGateway?.GatewayType, "Join", StringComparison.OrdinalIgnoreCase)
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
            var bulkDatasetUpdates = ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, mergedFieldValues, cursor);
            if (bulkDatasetUpdates.Count > 0)
            {
                mergedFieldValues = Merge(mergedFieldValues, bulkDatasetUpdates);
            }

            newInvocations.AddRange(ExecuteOnEnterSupportSystemActions(instance.InstanceId, definition, mergedFieldValues, cursor));
        }

        foreach (var joinGroup in newCursors
                     .Where(cursor => cursor.IsAtGateway)
                     .GroupBy(cursor => cursor.CurrentNodeKey, StringComparer.Ordinal))
        {
            var gateway = FindGateway(definition, joinGroup.Key);
            if (!string.Equals(gateway?.GatewayType, "Join", StringComparison.Ordinal))
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

            if (TryReleaseJoinIfReady(updated, definition, joinGateway, accessProfile) is { } released)
            {
                return released;
            }
        }

        SaveInstance(updated);
        Logger.LogInformation(
            "Split gateway '{Gateway}': instance {Id} fanned out to {Count} cursors.",
            splitGateway.Key, instance.InstanceId, newCursors.Count);

        return BuildEnvelope(updated, definition, accessProfile);
    }

    protected ServiceRequestResponseEnvelope HandleJoinGatewayAdvance(
        ServiceRequest instance,
        ServiceBlueprint definition,
        RouteFile arrivingTransition,
        ServiceBlueprintGatewayDefinition joinGateway,
        Dictionary<string, object?>? fieldValues,
        ActorProfile accessProfile)
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

            SaveInstance(waitingInstance);
            Logger.LogInformation(
                "Join gateway '{Gateway}': instance {Id} waiting ({Arrived}/{Required} queues).",
                gatewayKey, instance.InstanceId, arrivedQueues.Count, requiredQueues.Count);

            return BuildJoinWaitingEnvelope(waitingInstance, definition, joinGateway);
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

        return TryReleaseJoinIfReady(arrivedInstance, definition, joinGateway, accessProfile)
               ?? BuildJoinWaitingEnvelope(arrivedInstance, definition, joinGateway);
    }

    protected ServiceRequestResponseEnvelope BuildJoinWaitingEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ServiceBlueprintGatewayDefinition joinGateway)
    {
        var waitingContent = joinGateway.WaitingContent
                             ?? "Please wait while other parts of this blueprint are completed.";
        var pollMs = joinGateway.WaitingPollIntervalMs > 0 ? joinGateway.WaitingPollIntervalMs : 3000;
        var expectedSeconds = joinGateway.WaitingExpectedSeconds > 0 ? joinGateway.WaitingExpectedSeconds : 30;
        var allowDefer = joinGateway.WaitingDeferMessage is not null || joinGateway.WaitingAllowDefer;

        var waitingArrivals = instance.JoinArrivals.TryGetValue(joinGateway.Key, out var arr) ? arr : [];
        var requiredQueues = joinGateway.RequiredIncomingQueues ?? [];
        var pendingQueues = requiredQueues
            .Where(queue => instance.Cursors.All(c =>
                !(c.IsAtGateway
                  && string.Equals(c.CurrentNodeKey, joinGateway.Key, StringComparison.Ordinal)
                  && string.Equals(c.QueueKey, queue, StringComparison.Ordinal))))
            .ToArray();

        var statusContent = pendingQueues.Length > 0
            ? $"{waitingContent} Waiting for: {string.Join(", ", pendingQueues)}."
            : waitingContent;

        var render = new StepContent
        {
            StepType = "status-timeline",
            StateDisplayName = joinGateway.DisplayName,
            Components =
            [
                new ComponentRenderPayload
                {
                    Type = "waiting",
                    Content = statusContent,
                    ExpectedWaitSeconds = expectedSeconds,
                    PollIntervalMs = pollMs,
                    AllowDefer = allowDefer,
                    DeferMessage = joinGateway.WaitingDeferMessage
                }
            ],
            AvailableActions = Array.Empty<ServiceRequestAction>()
        };

        return new ServiceRequestResponseEnvelope
        {
            InstanceId = instance.InstanceId,
            ResponseState = "defer",
            StateVersion = instance.StateVersion,
            CorrelationId = instance.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            PollAfterMs = pollMs,
            Render = render,
            RequestPolicy = definition.RequestPolicy
        };
    }

    // ─── Support system helpers ──────────────────────────────────────────────

    /// <summary>
    /// Runs every <c>onEnter</c> <c>support-system-call</c> action declared on the stage a cursor
    /// just landed on, recording a <see cref="SupportSystemInvocation"/> for each successful
    /// start. Only wired into the multi-cursor paths (a support-system call only makes sense
    /// against a genuinely separate automation-queue cursor, per docs/guides/support-systems.md)
    /// — a single-queue blueprint has no automation actor for such an action to belong to, so
    /// this deliberately isn't called from the single-cursor "regular stage transition" path.
    /// </summary>
    private IReadOnlyList<SupportSystemInvocation> ExecuteOnEnterSupportSystemActions(
        string instanceId,
        ServiceBlueprint definition,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor)
    {
        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == cursor.CurrentNodeKey);
        if (stage?.Actions is not { Count: > 0 } actions)
        {
            return [];
        }

        var invocations = new List<SupportSystemInvocation>();
        foreach (var action in actions)
        {
            if (!string.Equals(action.Timing, "onEnter", StringComparison.Ordinal)
                || !string.Equals(action.Type, SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryExecuteSupportSystemCall(instanceId, fieldValues, cursor, action) is { } invocation)
            {
                invocations.Add(invocation);
            }
        }

        return invocations;
    }

    private SupportSystemInvocation? TryExecuteSupportSystemCall(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var supportSystemKey = action.Parameters["supportSystemKey"]?.GetValue<string>();
        var capabilityKey = action.Parameters["capabilityKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(supportSystemKey) || string.IsNullOrWhiteSpace(capabilityKey))
        {
            Logger.LogWarning(
                "support-system-call action on stage '{Stage}' is missing supportSystemKey/capabilityKey; skipped.",
                cursor.CurrentNodeKey);
            return null;
        }

        var capability = SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey);
        if (capability is null)
        {
            Logger.LogWarning(
                "support-system-call action on stage '{Stage}' references unregistered support system " +
                "'{System}'/capability '{Capability}'; skipped.",
                cursor.CurrentNodeKey, supportSystemKey, capabilityKey);
            return null;
        }

        if (!_supportSystemClients.TryGetValue(supportSystemKey, out var client))
        {
            Logger.LogWarning(
                "No ISupportSystemClient registered for support system '{System}'; skipped.", supportSystemKey);
            return null;
        }

        var inputFieldRefs = action.Parameters["inputs"]?.AsObject();
        var inputs = new Dictionary<string, SupportSystemInputValue>(StringComparer.Ordinal);
        foreach (var input in capability.Inputs)
        {
            var fieldKey = inputFieldRefs?[input.Key]?.GetValue<string>();
            var raw = fieldKey is not null ? fieldValues.GetValueOrDefault(fieldKey) : null;
            inputs[input.Key] = SupportSystemInputValue.Resolve(raw);
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var context = new SupportSystemInvocationContext
        {
            InstanceId = instanceId,
            InvocationId = invocationId,
            WebhookExpected = capability.SupportedCompletionModes.Contains(SupportSystemCompletionMode.Webhook)
        };

        SupportSystemInvocationReceipt receipt;
        try
        {
            receipt = client.InvokeAsync(capabilityKey, inputs, context).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Support system '{System}' capability '{Capability}' invocation failed for cursor '{Cursor}'.",
                supportSystemKey, capabilityKey, cursor.CursorId);
            return null;
        }

        return new SupportSystemInvocation
        {
            InvocationId = invocationId,
            SupportSystemKey = supportSystemKey,
            CapabilityKey = capabilityKey,
            CursorId = cursor.CursorId,
            StageKey = cursor.CurrentNodeKey,
            Receipt = receipt
        };
    }

    // ─── Bulk data review helpers ──────────────────────────────────────────────

    /// <summary>
    /// Runs every <c>bulk-dataset-ingest</c>/<c>bulk-dataset-materialize</c> onEnter action on the
    /// stage <paramref name="cursor"/> just landed on, in declared order, and returns the
    /// FieldValues delta they produced — the caller merges this into the same field-value set it
    /// hands <see cref="ExecuteOnEnterSupportSystemActions"/> right afterwards. Unlike a
    /// support-system-call, both action types here talk only to host-local infrastructure (an
    /// already-fetched file, an in-process dataset store) — no external round trip — so they
    /// execute synchronously and resolve within this same call, never as a tracked pending
    /// invocation. Deliberately called before <see cref="ExecuteOnEnterSupportSystemActions"/> at
    /// every call site: a <c>bulk-dataset-materialize</c> action's whole purpose is to refresh a
    /// file field before that same stage's own <c>support-system-call</c> action reads it on a
    /// resubmission loop (see docs/guides/bulk-data-review.md).
    /// </summary>
    private Dictionary<string, object?> ExecuteOnEnterBulkDatasetActions(
        string instanceId,
        ServiceBlueprint definition,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor)
    {
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == cursor.CurrentNodeKey);
        if (stage?.Actions is not { Count: > 0 } actions)
        {
            return updates;
        }

        var workingFieldValues = new Dictionary<string, object?>(fieldValues, StringComparer.Ordinal);

        foreach (var action in actions)
        {
            if (!string.Equals(action.Timing, "onEnter", StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyDictionary<string, object?> actionUpdates;
            if (string.Equals(action.Type, BulkDataActionTypes.BulkDatasetMaterialize, StringComparison.Ordinal))
            {
                actionUpdates = TryExecuteBulkDatasetMaterialize(instanceId, workingFieldValues, cursor, action);
            }
            else if (string.Equals(action.Type, BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
            {
                actionUpdates = TryExecuteBulkDatasetIngest(instanceId, workingFieldValues, cursor, action);
            }
            else
            {
                continue;
            }

            foreach (var (key, value) in actionUpdates)
            {
                updates[key] = value;
                workingFieldValues[key] = value;
            }
        }

        return updates;
    }

    /// <summary>
    /// Reconstructs the dataset named by this action's own <c>datasetIdField</c> and writes it
    /// into <c>targetFileField</c>. A safe no-op (returns no updates) when <c>datasetIdField</c>
    /// has no value yet — the expected case the first time this stage is entered, before
    /// anything's been ingested; the original upload already sitting in <c>targetFileField</c>
    /// goes through untouched.
    /// </summary>
    private IReadOnlyDictionary<string, object?> TryExecuteBulkDatasetMaterialize(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
        var targetFileField = action.Parameters["targetFileField"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(datasetIdField) || string.IsNullOrWhiteSpace(targetFileField))
        {
            Logger.LogWarning(
                "bulk-dataset-materialize action on stage '{Stage}' is missing datasetIdField/targetFileField; skipped.",
                cursor.CurrentNodeKey);
            return ImmutableFieldValueUpdates.Empty;
        }

        if (fieldValues.GetValueOrDefault(datasetIdField) is not string { Length: > 0 } datasetId)
        {
            return ImmutableFieldValueUpdates.Empty;
        }

        if (_bulkDatasetStore is null)
        {
            Logger.LogWarning(
                "No IBulkDatasetStore registered; bulk-dataset-materialize action on stage '{Stage}' skipped.",
                cursor.CurrentNodeKey);
            return ImmutableFieldValueUpdates.Empty;
        }

        ServiceRequestFileReference materialized;
        try
        {
            materialized = _bulkDatasetStore
                .MaterializeAsync(instanceId, datasetId, targetFileField, $"{targetFileField}.csv", sanitizeForHumanExport: false)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex, "bulk-dataset-materialize failed for dataset '{DatasetId}' on cursor '{Cursor}'.",
                datasetId, cursor.CursorId);
            return ImmutableFieldValueUpdates.Empty;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { [targetFileField] = materialized };
    }

    /// <summary>
    /// Parses this action's <c>sourceFileField</c> against its declared <c>columns</c> into a
    /// fresh dataset via <see cref="IBulkDatasetStore.IngestAsync"/>, and returns the resulting
    /// dataset id plus any declared summary counts as field-value updates.
    /// </summary>
    private IReadOnlyDictionary<string, object?> TryExecuteBulkDatasetIngest(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var sourceFileField = action.Parameters["sourceFileField"]?.GetValue<string>();
        var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourceFileField) || string.IsNullOrWhiteSpace(datasetIdField))
        {
            Logger.LogWarning(
                "bulk-dataset-ingest action on stage '{Stage}' is missing sourceFileField/datasetIdField; skipped.",
                cursor.CurrentNodeKey);
            return ImmutableFieldValueUpdates.Empty;
        }

        if (_bulkDatasetStore is null)
        {
            Logger.LogWarning(
                "No IBulkDatasetStore registered; bulk-dataset-ingest action on stage '{Stage}' skipped.",
                cursor.CurrentNodeKey);
            return ImmutableFieldValueUpdates.Empty;
        }

        var sourceFile = ServiceRequestFileReference.FromFieldValue(fieldValues.GetValueOrDefault(sourceFileField));
        if (sourceFile is null)
        {
            Logger.LogWarning(
                "bulk-dataset-ingest action on stage '{Stage}' references field '{Field}', which has no file value; skipped.",
                cursor.CurrentNodeKey, sourceFileField);
            return ImmutableFieldValueUpdates.Empty;
        }

        var columns = ParseBulkDatasetColumns(action);
        if (columns.Count == 0)
        {
            Logger.LogWarning(
                "bulk-dataset-ingest action on stage '{Stage}' declares no valid columns; skipped.",
                cursor.CurrentNodeKey);
            return ImmutableFieldValueUpdates.Empty;
        }

        BulkDatasetIngestResult result;
        try
        {
            result = _bulkDatasetStore.IngestAsync(instanceId, sourceFile, columns).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex, "bulk-dataset-ingest failed for field '{Field}' on cursor '{Cursor}'.",
                sourceFileField, cursor.CursorId);
            return ImmutableFieldValueUpdates.Empty;
        }

        if (!result.Succeeded)
        {
            Logger.LogWarning(
                "bulk-dataset-ingest failed for field '{Field}' on cursor '{Cursor}': {Reason}",
                sourceFileField, cursor.CursorId, result.FailureReason);
            return ImmutableFieldValueUpdates.Empty;
        }

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal) { [datasetIdField] = result.DatasetId };
        AddDeclaredCountUpdate(action, "errorCountField", result.Summary!.ErrorRowCount, updates);
        AddDeclaredCountUpdate(action, "warningCountField", result.Summary.WarningRowCount, updates);
        AddDeclaredCountUpdate(action, "acceptedCountField", result.Summary.AcceptedRowCount, updates);

        return updates;
    }

    private static void AddDeclaredCountUpdate(
        ActionDefinition action, string paramName, int value, Dictionary<string, object?> updates)
    {
        var fieldKey = action.Parameters[paramName]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fieldKey))
        {
            // decimal, not int: CalculationEvaluator.ValuesEqual only coerces when BOTH sides of
            // "=" are decimal — a numeric literal in a showWhen/calculation expression parses as
            // decimal, so a plain boxed int here would silently compare unequal to it even when
            // numerically equal (found live: njf-contributions.json's "Accept and finish" route,
            // showWhen: "contributionsErrorCount = 0", never became visible even once the count
            // genuinely reached zero). Matches ToFieldValues' own JsonValue-decimal handling —
            // "decimal for numbers in FieldValues" is this engine's established convention, not
            // something to work around per call site.
            updates[fieldKey] = (decimal)value;
        }
    }

    private static IReadOnlyList<BulkDatasetColumnDescriptor> ParseBulkDatasetColumns(ActionDefinition action)
    {
        var columns = new List<BulkDatasetColumnDescriptor>();
        foreach (var columnNode in action.Parameters["columns"]?.AsArray() ?? [])
        {
            var column = columnNode?.AsObject();
            var key = column?["key"]?.GetValue<string>();
            var title = column?["title"]?.GetValue<string>();
            var roleValue = column?["role"]?.GetValue<string>();
            var valueKindValue = column?["valueKind"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(title)
                || !Enum.TryParse<BulkDatasetColumnRole>(roleValue, out var role)
                || !Enum.TryParse<ComponentPropertyValueKind>(valueKindValue, out var valueKind))
            {
                continue;
            }

            columns.Add(new BulkDatasetColumnDescriptor
            {
                Key = key,
                Title = title,
                ValueKind = valueKind,
                Format = column?["format"]?.GetValue<string>(),
                Role = role,
                Visible = column?["visible"]?.GetValue<bool>() ?? true,
                Editable = column?["editable"]?.GetValue<bool>() ?? false,
            });
        }

        return columns;
    }

    private static class ImmutableFieldValueUpdates
    {
        public static readonly IReadOnlyDictionary<string, object?> Empty =
            new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    // ─── end Bulk data review helpers ──────────────────────────────────────────

    /// <summary>
    /// Gives any support-system invocation still blocking <paramref name="joinGateway"/> a chance
    /// to resolve via poll, the generic counterpart to the webhook receiver resolving one
    /// asynchronously — called every time a client re-polls a waiting join gateway (see
    /// <see cref="BuildEnvelope"/>). Only checks invocations whose capability actually declared
    /// <see cref="SupportSystemCompletionMode.Poll"/> support; a webhook-only capability is never
    /// polled, it can only resolve via <see cref="ResolveSupportSystemOutcome"/>. Returns true if
    /// at least one invocation resolved (and therefore state has already been saved, possibly
    /// including a full join release) — the caller should re-derive its response from a fresh
    /// read rather than the <paramref name="instance"/> it started with.
    /// </summary>
    private bool TryPollResolveSupportSystemInvocations(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ServiceBlueprintGatewayDefinition joinGateway)
    {
        var requiredQueues = joinGateway.RequiredIncomingQueues ?? [];
        var pendingQueues = requiredQueues
            .Where(queue => instance.Cursors.All(c =>
                !(c.IsAtGateway
                  && string.Equals(c.CurrentNodeKey, joinGateway.Key, StringComparison.Ordinal)
                  && string.Equals(c.QueueKey, queue, StringComparison.Ordinal))))
            .ToHashSet(StringComparer.Ordinal);

        if (pendingQueues.Count == 0)
        {
            return false;
        }

        var pendingCursorIds = instance.Cursors
            .Where(c => !c.IsAtGateway && pendingQueues.Contains(c.QueueKey))
            .Select(c => c.CursorId)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = instance.SupportSystemInvocations
            .Where(invocation => !invocation.Resolved && pendingCursorIds.Contains(invocation.CursorId))
            .ToList();

        var resolvedAny = false;
        foreach (var invocation in candidates)
        {
            var capability = SupportSystemRegistry.FindCapability(invocation.SupportSystemKey, invocation.CapabilityKey);
            if (capability is null
                || !capability.SupportedCompletionModes.Contains(SupportSystemCompletionMode.Poll)
                || invocation.Receipt is null
                || !_supportSystemClients.TryGetValue(invocation.SupportSystemKey, out var client))
            {
                continue;
            }

            SupportSystemOutcome? outcome;
            try
            {
                outcome = client.CheckStatusAsync(invocation.CapabilityKey, invocation.Receipt).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Support system '{System}' capability '{Capability}' status check failed for invocation '{Invocation}'.",
                    invocation.SupportSystemKey, invocation.CapabilityKey, invocation.InvocationId);
                continue;
            }

            if (outcome is null)
            {
                continue;
            }

            var resolution = ResolveSupportSystemOutcome(invocation.InvocationId, outcome.OutcomeKey, outcome.ResultPayload);
            resolvedAny = resolvedAny || resolution.ResponseState != "error";
        }

        return resolvedAny;
    }

    /// <summary>
    /// Delivers a support-system capability's outcome back into the blueprint — the single code
    /// path both the poll-check hook (<see cref="TryPollResolveSupportSystemInvocations"/>) and
    /// the generic webhook receiver (<c>Wayfinder.Engine.Api</c>) call, so "what did the external
    /// system decide" is resolved identically regardless of which mechanism delivered it. Looks
    /// the owning instance up by <paramref name="invocationId"/> alone — a webhook callback only
    /// ever carries that one opaque token, never the instance id — then advances the waiting
    /// automation cursor exactly as if that cursor's own actor had called
    /// <see cref="Advance(string,string,string,ActorProfile,string,int,Dictionary{string,object?}?)"/>
    /// with <paramref name="outcomeKey"/> as the action, retrying under this engine's normal
    /// optimistic concurrency if something else updated the instance in between.
    /// </summary>
    public ServiceRequestResponseEnvelope ResolveSupportSystemOutcome(
        string invocationId,
        string outcomeKey,
        JsonObject? resultPayload = null)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var owner = _instanceStore.GetAll().FirstOrDefault(
                i => i.SupportSystemInvocations.Any(inv => inv.InvocationId == invocationId && !inv.Resolved));

            if (owner is null)
            {
                return ErrorEnvelope(
                    $"No pending support-system invocation '{invocationId}' found.",
                    "SUPPORT_SYSTEM_INVOCATION_NOT_FOUND");
            }

            var invocation = owner.SupportSystemInvocations.First(inv => inv.InvocationId == invocationId);
            var capability = SupportSystemRegistry.FindCapability(invocation.SupportSystemKey, invocation.CapabilityKey);
            if (capability is null || capability.Outcomes.All(o => o.Key != outcomeKey))
            {
                return ErrorEnvelope(
                    $"'{outcomeKey}' is not a declared outcome of capability '{invocation.CapabilityKey}' on " +
                    $"support system '{invocation.SupportSystemKey}'.",
                    "SUPPORT_SYSTEM_INVALID_OUTCOME");
            }

            // Mark resolved and save before advancing — Advance() always re-reads the instance
            // fresh from the store by id, so this is the only way this mutation actually reaches
            // it. Marking it here, ahead of the Advance() call below, also makes a second
            // concurrent delivery for the same invocation (poll racing a webhook for a
            // Both-completion-mode capability) a safe no-op instead of a double-advance: it will
            // no longer find an unresolved invocation on its own retry.
            //
            // resultPayload is merged into FieldValues directly here, NOT passed as Advance()'s
            // own fieldValues argument — that argument is validated against the CURRENT stage's
            // (the support-system-call stage's own) declared fields, a whitelist a result payload
            // key has no reason to appear in, so it would always be rejected as "unknown field".
            // Merging it into already-persisted instance state first sidesteps that check exactly
            // the way any other previously-saved field value does.
            var withResolvedInvocation = owner with
            {
                SupportSystemInvocations = owner.SupportSystemInvocations
                    .Select(inv => inv.InvocationId == invocationId
                        ? inv with { Resolved = true, OutcomeKey = outcomeKey }
                        : inv)
                    .ToArray(),
                FieldValues = resultPayload is null ? owner.FieldValues : Merge(owner.FieldValues, ToFieldValues(resultPayload)),
                StateVersion = owner.StateVersion + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            SaveInstance(withResolvedInvocation);

            var advanced = Advance(
                withResolvedInvocation.InstanceId,
                withResolvedInvocation.TenantId,
                withResolvedInvocation.UserId,
                ActorProfile.UnrestrictedOwner,
                outcomeKey,
                withResolvedInvocation.StateVersion,
                null);

            var isConflict = advanced.ResponseState == "error"
                && advanced.Problems.Any(p => p.Code == "VERSION_MISMATCH");
            if (!isConflict)
            {
                return advanced;
            }
        }

        return ErrorEnvelope(
            $"Could not resolve support-system invocation '{invocationId}' after {maxAttempts} attempts due to concurrent updates.",
            "SUPPORT_SYSTEM_RESOLUTION_CONFLICT");
    }

    private static Dictionary<string, object?> ToFieldValues(JsonObject payload)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            if (value is null)
            {
                result[key] = null;
            }
            else if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var s))
            {
                result[key] = s;
            }
            else if (value is JsonValue boolValue && boolValue.TryGetValue<bool>(out var b))
            {
                result[key] = b;
            }
            else if (value is JsonValue decimalValue && decimalValue.TryGetValue<decimal>(out var d))
            {
                result[key] = d;
            }
            else
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }

    // ─── end Support system helpers ──────────────────────────────────────────

    private ServiceRequestResponseEnvelope? TryReleaseJoinIfReady(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ServiceBlueprintGatewayDefinition joinGateway,
        ActorProfile accessProfile)
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
            return ErrorEnvelope(
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
                return ErrorEnvelope(
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
            var bulkDatasetUpdates = ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, releasedFieldValues, cursor);
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

        SaveInstance(releasedInstance);
        return BuildEnvelope(releasedInstance, definition, accessProfile);
    }

    private static IReadOnlyList<RequestCursor> MoveCursor(
        IReadOnlyList<RequestCursor> cursors,
        string? cursorId,
        string newNodeKey,
        bool isAtGateway,
        string? arrivedViaAction = null)
    {
        if (cursorId == null)
            return cursors;

        return cursors
            .Select(c => c.CursorId == cursorId
                ? c with { CurrentNodeKey = newNodeKey, IsAtGateway = isAtGateway, ArrivedViaAction = isAtGateway ? arrivedViaAction : null }
                : c)
            .ToArray();
    }

    private static string? FirstActiveStageCursorKey(IReadOnlyList<RequestCursor> cursors) =>
        cursors.FirstOrDefault(c => !c.IsAtGateway)?.CurrentNodeKey;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    // ─── end Gateway helpers ──────────────────────────────────────────────────

    private static bool IsTerminalInstance(ServiceRequest instance, ServiceBlueprint definition)
    {
        var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == instance.CurrentStage);
        return currentStage != null && currentStage.Components.InferStepType() == "confirmation";
    }

    private ServiceRequest? FindLatestInstance(string tenantId, string userId, string blueprintKey) =>
        _instanceStore.GetAll()
            .Where(instance =>
                string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(instance.UserId, userId, StringComparison.Ordinal)
                && string.Equals(instance.BlueprintKey, blueprintKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.UpdatedAt)
            .ThenByDescending(instance => instance.CreatedAt)
            .FirstOrDefault();

    private static Dictionary<string, object?> Merge(
        Dictionary<string, object?> existing,
        Dictionary<string, object?>? incoming)
    {
        if (incoming == null || incoming.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, object?>(existing);
        foreach (var kvp in incoming)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return merged;
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
