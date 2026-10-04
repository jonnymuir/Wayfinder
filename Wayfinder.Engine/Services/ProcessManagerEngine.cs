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
using static Wayfinder.Engine.Services.BlueprintLookup;
using static Wayfinder.Engine.Services.QueueAccess;
using static Wayfinder.Engine.Services.FieldValueMerge;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Generic in-memory runtime engine that executes Wayfinder service blueprints.
/// </summary>
public class ProcessManagerEngine : IProcessManager, IEnvelopeSource
{
    private readonly IServiceContentSanitizer _sanitizer;
    private readonly BlueprintRegistry _registry;
    private readonly InstanceRepository _instances;
    private readonly StageCalculations _calculations;
    private readonly WorkItemFinder _workItems;
    private readonly StageRenderer _renderer;
    private readonly BulkDatasetActions _bulkDatasets;
    private readonly SupportSystemActions _supportSystems;
    private readonly InstanceAdmin _admin;
    private readonly WorkQueues _queues;
    private readonly WorkAllocation _allocation;
    private readonly Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?>? _serviceInputsResolver;
    private readonly Dictionary<string, ISupportSystemClient> _supportSystemClients;
    private readonly IBulkDatasetStore? _bulkDatasetStore;
    private readonly Dictionary<string, IRequestConcurrencyPolicy> _requestConcurrencyPolicies;

    public ProcessManagerEngine(
        ILogger logger,
        IServiceBlueprintStore definitionStore,
        IServiceContentSanitizer sanitizer,
        Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?>? serviceInputsResolver = null,
        IServiceRequestStore? instanceStore = null,
        IEnumerable<ISupportSystemClient>? supportSystemClients = null,
        IBulkDatasetStore? bulkDatasetStore = null,
        IEnumerable<IRequestConcurrencyPolicy>? requestConcurrencyPolicies = null,
        IAuditLogStore? auditLogStore = null)
    {
        Logger = logger;
        _sanitizer = sanitizer;
        _serviceInputsResolver = serviceInputsResolver;
        _supportSystemClients = (supportSystemClients ?? [])
            .ToDictionary(client => client.SupportSystemKey, StringComparer.Ordinal);
        _bulkDatasetStore = bulkDatasetStore;
        _requestConcurrencyPolicies = (requestConcurrencyPolicies ?? [])
            .SelectMany(policy => policy.DefinitionKeys.Select(key => (key, policy)))
            .ToDictionary(pair => pair.key, pair => pair.policy, StringComparer.OrdinalIgnoreCase);
        _registry = new BlueprintRegistry(definitionStore, logger);
        _instances = new InstanceRepository(
            instanceStore ?? new InMemoryServiceRequestStore(),
            auditLogStore ?? new InMemoryAuditLogStore(),
            _registry);
        _calculations = new StageCalculations(logger, _instances, ResolveServiceInputs);
        _workItems = new WorkItemFinder(_calculations);
        _renderer = new StageRenderer(sanitizer, _calculations, logger);
        _bulkDatasets = new BulkDatasetActions(bulkDatasetStore, logger);
        _supportSystems = new SupportSystemActions(_supportSystemClients, logger);
        _admin = new InstanceAdmin(_instances, _registry, logger);
        _queues = new WorkQueues(_instances, _registry, _workItems, TryPollResolveSupportSystemInvocations);
        _allocation = new WorkAllocation(_instances, _registry, _workItems, this);
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
        if (!_registry.TryGet(blueprintKey, out var definition))
        {
            Logger.LogWarning("Service blueprint not found: {Key}", blueprintKey);
            return Envelopes.Error(
                $"Blueprint '{blueprintKey}' is not registered with this application.",
                "DEFINITION_NOT_FOUND");
        }

        if (!string.IsNullOrEmpty(instanceId))
        {
            if (!_instances.TryGet(instanceId, out var specificInstance))
            {
                return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(specificInstance, tenantId, userId, accessProfile))
            {
                return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
            }

            Logger.LogInformation("Resuming specific instance {Id}", instanceId);
            return BuildEnvelope(specificInstance, definition, accessProfile, userId);
        }

        var existingInstance = FindLatestInstance(tenantId, userId, blueprintKey, accessProfile);

        if (!CanStartInitialState(definition, accessProfile))
        {
            return Envelopes.Error("Access denied to start this queue.", "ACCESS_DENIED");
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
                "action=start-new");
        }

        if (string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
        {
            if (existingInstance is not null)
            {
                Logger.LogInformation("Resuming existing instance {Id} (action=resume)", existingInstance.InstanceId);
                return BuildEnvelope(existingInstance, definition, accessProfile, userId);
            }

            return CreateAndRegisterNewInstance(
                blueprintKey,
                tenantId,
                userId,
                definition,
                accessProfile,
                action,
                "action=resume, no existing");
        }

        // A host-registered custom policy (see IRequestConcurrencyPolicy's own remarks) takes over
        // entirely for the blueprints it names — every other blueprint never touches this
        // dictionary lookup at all and falls straight through to the built-in switch below,
        // completely unaffected. Explicit start-new/resume already returned above regardless of
        // policy, matching how the built-in single/multiple/prompt switch already treats them.
        if (_requestConcurrencyPolicies.TryGetValue(blueprintKey, out var customPolicy))
        {
            var candidateInstances = _instances.GetAll()
                .Where(instance =>
                    string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal)
                    && string.Equals(instance.BlueprintKey, blueprintKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var decision = customPolicy
                .EvaluateAsync(definition, tenantId, userId, accessProfile, candidateInstances)
                .GetAwaiter().GetResult();

            switch (decision.Outcome)
            {
                case RequestConcurrencyOutcome.ReuseExisting:
                    return BuildEnvelope(
                        decision.ExistingInstance ?? throw new InvalidOperationException(
                            $"{customPolicy.GetType().Name} returned ReuseExisting with no ExistingInstance."),
                        definition, accessProfile, userId);
                case RequestConcurrencyOutcome.Deny:
                    return Envelopes.Error(
                        decision.DenyReason ?? "This request was denied by a registered concurrency policy.",
                        "CONCURRENCY_POLICY_DENIED");
                case RequestConcurrencyOutcome.AllowNew:
                default:
                    return CreateAndRegisterNewInstance(
                        blueprintKey,
                        tenantId,
                        userId,
                        definition,
                        accessProfile,
                        action,
                        "custom concurrency policy: AllowNew");
            }
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
                "policy=multiple");
        }

        if (string.Equals(policy, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (existingInstance is not null)
            {
                var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == existingInstance.CurrentStage);

                if (!IsTerminalInstance(existingInstance, definition, accessProfile))
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
                        AllowManualRestart = definition.AllowManualRestart,
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
                "policy=prompt, no active");
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
                "no existing instance");
        }

        // "single" means at most one instance per user for this blueprint, full stop — once it
        // reaches a terminal stage it keeps being shown on every subsequent visit (the community
        // enquiry demo depends on this: a member returning to the page sees "Thank you", not a
        // silently-reset blank form). ServiceRequestPageController's PRG redirect after a POST
        // relies on this same fallthrough to show the confirmation page for the visit that just
        // submitted it.
        return BuildEnvelope(existingInstance, definition, accessProfile, userId);
    }

    /// <summary>
    /// The "start" affordance a genuine ambient <see cref="GetCurrent(string,string,string,ActorProfile,string?,string?)"/>
    /// (a "continue where I left off" link) deliberately isn't: an ordinary visit must keep
    /// showing a terminal instance forever under "single" (a returning citizen sees "Thank you",
    /// not a silently-reset blank form — see the comment just above this method), but a distinct
    /// "start a new one" link shouldn't hand back a stale confirmation from months ago either. A
    /// non-terminal existing instance is reinstated exactly as ambient <c>GetCurrent</c> already
    /// does — never abandons in-progress work; only a genuinely terminal (or absent) existing
    /// instance triggers a real fresh one, via the same explicit <c>action: "start-new"</c> that
    /// already exists for this (<see cref="ServiceBlueprintSimulationRunner"/> is the other caller
    /// of that action, and needs it to stay unconditionally-always-fresh — that's exactly why this
    /// is a new method rather than a change to what "start-new" itself means).
    /// </summary>
    public ServiceRequestResponseEnvelope GetCurrentOrStartFresh(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile)
    {
        var existingInstance = FindLatestInstance(tenantId, userId, blueprintKey, accessProfile);
        if (existingInstance is not null
            && _registry.TryGet(blueprintKey, out var definition)
            && IsTerminalInstance(existingInstance, definition, accessProfile))
        {
            return GetCurrent(blueprintKey, tenantId, userId, accessProfile, action: "start-new");
        }

        return GetCurrent(blueprintKey, tenantId, userId, accessProfile);
    }

    /// <summary>
    /// The gated entry point a citizen-facing surface must use for an untrusted <c>action:
    /// "start-new"</c> request (e.g. a "Start again" link's query string, or the "prompt"-policy
    /// instance picker's own "Start a new request" choice) — refuses it outright unless
    /// <see cref="ServiceBlueprint.AllowManualRestart"/> is set, falling back to plain ambient
    /// <c>GetCurrent</c> (never an error — a disallowed or stale <c>?action=start-new</c> link must
    /// not break the page, it must just not do anything special). Once allowed, this hands off to
    /// the same raw, unconditional <c>action: "start-new"</c> handling <c>GetCurrent</c> has always
    /// had — deliberately NOT <see cref="GetCurrentOrStartFresh"/>'s own "never abandon a
    /// non-terminal instance" restriction, which would silently turn the "prompt" policy's own
    /// picker choice into a no-op (it exists specifically to let a citizen abandon a genuinely
    /// in-progress instance when they consciously choose to, having just been shown it exists —
    /// that terminal-only restriction solves a different problem: an *ambient*, no-explicit-action
    /// render must never surprise-abandon work nobody asked to abandon). See
    /// <see cref="ServiceBlueprint.AllowManualRestart"/>'s own remarks for why this needs to be an
    /// explicit opt-in rather than available to every blueprint by default.
    /// </summary>
    public ServiceRequestResponseEnvelope GetCurrentOrManualRestart(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile)
    {
        if (!_registry.TryGet(blueprintKey, out var definition) || !definition.AllowManualRestart)
        {
            Logger.LogWarning(
                "Manual restart (action=start-new) requested for blueprint '{Key}', which does not " +
                "declare allowManualRestart — ignoring and resuming ambient state instead.",
                blueprintKey);
            return GetCurrent(blueprintKey, tenantId, userId, accessProfile);
        }

        return GetCurrent(blueprintKey, tenantId, userId, accessProfile, action: "start-new");
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
        if (!_instances.TryGet(instanceId, out var instance))
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

        if (!_registry.TryGet(instance.BlueprintKey, out var definition))
        {
            return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
        }

        if (action.StartsWith("change:", StringComparison.OrdinalIgnoreCase))
        {
            var targetStageKey = action["change:".Length..];
            var targetStage = definition.Stages.FirstOrDefault(s => s.StageKey == targetStageKey);
            if (targetStage is null)
            {
                return Envelopes.Error($"State '{targetStageKey}' not found in definition.", "STATE_NOT_FOUND");
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

            // Atomic against a concurrent writer between the version check above and this save —
            // unlike a plain SaveInstance, which would silently overwrite whatever another caller
            // just wrote in that window. See IServiceRequestStore.TrySaveIfVersionMatches.
            var jumpAuditEvent = AuditEvents.Transition(
                instance.InstanceId, userId, cursorId: null,
                fromStageKey: instance.CurrentStage, toStageKey: targetStageKey, action: action, detail: "admin change-link jump");
            var savedJumped = _instances.TrySaveIfVersionMatches(jumped, userId, instance.StateVersion, jumpAuditEvent);
            if (savedJumped is null)
            {
                return Envelopes.Error(
                    $"State version mismatch: expected {expectedStateVersion}, actual has changed concurrently.",
                    "VERSION_MISMATCH");
            }

            Logger.LogInformation(
                "Change-link: jumped instance {Id} to stage '{State}'",
                instanceId,
                targetStageKey);
            return BuildEnvelope(savedJumped, definition, accessProfile, userId);
        }

        var visibleWorkItem = _workItems.FindAccessibleWorkItems(instance, definition, accessProfile, userId)
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

        // Never trust the client: whatever fieldValues arrived here — from a legitimate form
        // post or a tampered one — is validated against the CURRENT stage's own authoritative
        // field declarations before anything else touches instance state. Reuses the exact same
        // methods rendering already calls for this stage, so validation can never drift from
        // what was actually rendered, and needs no per-host wiring to be enforced.
        var currentStage = definition.Stages.FirstOrDefault(s => s.StageKey == visibleWorkItem.StageKey);
        if (currentStage is not null)
        {
            var currentCalc = _calculations.EvaluateDefinitionCalculations(instance, definition, currentStage);
            var currentComponents = _renderer.BuildComponents(currentStage.Components, instance.FieldValues, currentCalc);
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
                return BuildEnvelope(previewInstance, definition, accessProfile, userId) with { Problems = problems };
            }

            // Declarative cross-field business rules (StageDefinition.Validations) — the
            // engine-native alternative to a host's ValidateAdvance override, checked once
            // field-level validation has already passed. Evaluated on the same merge of
            // persisted + just-submitted values FieldValueValidator above just accepted, never
            // on stale persisted data or on anything the client could claim was pre-checked.
            var stageValidationProblems = _calculations.EvaluateStageValidations(instance, definition, currentStage, fieldValues, action);
            if (stageValidationProblems.Count > 0)
            {
                var previewInstance = instance with { FieldValues = Merge(instance.FieldValues, fieldValues) };
                return BuildEnvelope(previewInstance, definition, accessProfile, userId) with { Problems = stageValidationProblems };
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
            return nextGateway.GatewayType == GatewayKind.Split
                ? HandleSplitGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId)
                : HandleJoinGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId);
        }

        // Regular stage transition (single- or multi-cursor).
        if (instance.Cursors.Count > 0)
        {
            // Multi-cursor: advance only the cursor currently at this stage. A plain (non-gateway)
            // hop can still cross into a differently-queued stage with no gateway in between — the
            // cursor's own QueueKey must follow it there, or FindAccessibleWorkItems (which resolves
            // ownership/eligibility from cursor.QueueKey directly for a non-gateway cursor) keeps
            // checking the stage the cursor just left. Found live: PickupWorkItem mints a real
            // cursor the first time an instance without one is picked up, which permanently switches
            // it onto this multi-cursor path even for a blueprint with no gateways at all — before
            // mandatory pickup, such an instance always stayed on the Cursors.Count == 0 path, which
            // re-derives queueKey fresh from the stage on every call and never went stale.
            var sourceCursor = instance.Cursors.FirstOrDefault(c =>
                c.CurrentNodeKey == visibleWorkItem.StageKey && !c.IsAtGateway);
            var targetStage = definition.Stages.FirstOrDefault(s => s.StageKey == transition.ToState);
            var updatedCursors = MoveCursor(
                instance.Cursors, sourceCursor?.CursorId, transition.ToState, isAtGateway: false,
                newQueueKey: GetQueueKey(targetStage));
            var primaryStage = FirstActiveStageCursorKey(updatedCursors) ?? transition.ToState;
            var mergedMultiFieldValues = Merge(instance.FieldValues, fieldValues);
            var movedCursor = updatedCursors.FirstOrDefault(c => c.CursorId == sourceCursor?.CursorId);
            var newInvocations = movedCursor is not null
                ? _supportSystems.ExecuteOnEnterSupportSystemActions(instanceId, definition, mergedMultiFieldValues, movedCursor)
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
            var multiAuditEvent = AuditEvents.Transition(
                instance.InstanceId, userId, cursorId: sourceCursor?.CursorId,
                fromStageKey: visibleWorkItem.StageKey, toStageKey: transition.ToState, action: transition.Action);
            var savedMulti = _instances.TrySaveIfVersionMatches(updatedMulti, userId, instance.StateVersion, multiAuditEvent);
            if (savedMulti is null)
            {
                return Envelopes.Error(
                    $"State version mismatch: expected {expectedStateVersion}, actual has changed concurrently.",
                    "VERSION_MISMATCH");
            }

            Logger.LogInformation(
                "Multi-cursor advance instance {Id}: cursor {CursorId} → {To}",
                instanceId, sourceCursor?.CursorId ?? "(none)", transition.ToState);
            return BuildEnvelope(savedMulti, definition, accessProfile, userId);
        }

        var updated = instance with
        {
            CurrentStage = transition.ToState,
            StateVersion = instance.StateVersion + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            FieldValues = Merge(instance.FieldValues, fieldValues)
        };

        var advanceAuditEvent = AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: visibleWorkItem.StageKey, toStageKey: transition.ToState, action: transition.Action);
        var savedUpdated = _instances.TrySaveIfVersionMatches(updated, userId, instance.StateVersion, advanceAuditEvent);
        if (savedUpdated is null)
        {
            return Envelopes.Error(
                $"State version mismatch: expected {expectedStateVersion}, actual has changed concurrently.",
                "VERSION_MISMATCH");
        }

        Logger.LogInformation(
            "Advanced instance {Id}: {From} → {To}",
            instanceId,
            visibleWorkItem.StageKey,
            transition.ToState);

        return BuildEnvelope(savedUpdated, definition, accessProfile, userId);
    }

    /// <inheritdoc cref="IProcessManager.TryGetAccessibleInstance"/>
    public ServiceRequest? TryGetAccessibleInstance(string instanceId, string tenantId, string userId, ActorProfile accessProfile)
    {
        if (!_instances.TryGet(instanceId, out var instance))
        {
            return null;
        }

        return CanAccessInstance(instance, tenantId, userId, accessProfile) ? instance : null;
    }

    public IEnumerable<ServiceRequest> GetAllInstances() => _instances.GetAll();

    /// <inheritdoc cref="IProcessManager.SearchInstancesForAdmin"/>
    public ServiceRequestAdminListEnvelope SearchInstancesForAdmin(ServiceRequestAdminQuery query) =>
        _admin.SearchInstancesForAdmin(query);

    /// <inheritdoc cref="IProcessManager.AbortInstance"/>
    public bool AbortInstance(string instanceId, string reason, string abortedByUserId) =>
        _admin.AbortInstance(instanceId, reason, abortedByUserId);

    public ServiceRequestListEnvelope GetInstances(string tenantId, string userId) =>
        _admin.GetInstances(tenantId, userId);

    public IReadOnlyList<string> ClaimInstances(string tenantId, string fromUserId, string toUserId) =>
        _admin.ClaimInstances(tenantId, fromUserId, toUserId);

    /// <summary>
    /// See <see cref="IProcessManager.SyncServiceFields"/>. Every key in <paramref name="updates"/>
    /// is checked against <c>definition.Calculations.Fields</c> before anything is written — the
    /// sole authorization boundary this method has — so a caller can never use this to smuggle a
    /// write into a captured-input or formula-computed field.
    /// </summary>
    public ServiceRequestResponseEnvelope SyncServiceFields(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile,
        Dictionary<string, object?> updates)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!_instances.TryGet(instanceId, out var instance))
            {
                return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
            {
                return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
            }

            if (!_registry.TryGet(instance.BlueprintKey, out var definition))
            {
                return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
            }

            var serviceFields = definition.Calculations?.Fields;
            foreach (var key in updates.Keys)
            {
                if (serviceFields is null
                    || !serviceFields.TryGetValue(key, out var field)
                    || !string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase))
                {
                    return Envelopes.Error(
                        $"Field '{key}' is not declared with source: \"service\" on this blueprint and cannot be synced.",
                        "NOT_SERVICE_FIELD");
                }
            }

            var updatedInstance = instance with
            {
                FieldValues = Merge(instance.FieldValues, updates),
                StateVersion = instance.StateVersion + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var savedSync = _instances.TrySaveIfVersionMatches(updatedInstance, userId, instance.StateVersion, auditEvent: null);
            if (savedSync is not null)
            {
                return BuildEnvelope(savedSync, definition, accessProfile, userId);
            }
        }

        return Envelopes.Error(
            $"Could not sync fields on '{instanceId}' after {maxAttempts} attempts due to concurrent updates.",
            "SYNC_CONFLICT");
    }

    /// <summary>See <see cref="IProcessManager.SyncBulkDatasetSyncState"/>.</summary>
    public ServiceRequestResponseEnvelope SyncBulkDatasetSyncState(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile, string datasetId)
    {
        if (!_instances.TryGet(instanceId, out var instance))
        {
            return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
        }

        if (!_registry.TryGet(instance.BlueprintKey, out var definition))
        {
            return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
        }

        var dirtyCountField = BulkDatasetActions.FindDeclaringIngestAction(definition, instance.FieldValues, datasetId)
            ?.Parameters["dirtyCountField"]?.GetValue<string>();

        if (_bulkDatasetStore is null || string.IsNullOrWhiteSpace(dirtyCountField))
        {
            // Not opted in for this blueprint/dataset — same "declared-but-unused count field is a
            // no-op" convention errorCountField/warningCountField/acceptedCountField already follow.
            return GetCurrent(instance.BlueprintKey, tenantId, userId, accessProfile, instanceId);
        }

        var summary = _bulkDatasetStore.GetSummaryAsync(instanceId, datasetId).GetAwaiter().GetResult();
        if (summary is null)
        {
            return GetCurrent(instance.BlueprintKey, tenantId, userId, accessProfile, instanceId);
        }

        return SyncServiceFields(
            instanceId, tenantId, userId, accessProfile,
            new Dictionary<string, object?> { [dirtyCountField] = (decimal)summary.DirtyRowCount });
    }

    /// <inheritdoc cref="IProcessManager.PickupWorkItem"/>
    public ServiceRequestResponseEnvelope PickupWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile) =>
        _allocation.PickupWorkItem(instanceId, cursorId, tenantId, userId, accessProfile);

    /// <inheritdoc cref="IProcessManager.PutbackWorkItem"/>
    public ServiceRequestResponseEnvelope PutbackWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile) =>
        _allocation.PutbackWorkItem(instanceId, cursorId, tenantId, userId, accessProfile);

    /// <inheritdoc cref="IProcessManager.PickupNextAvailableWorkItem"/>
    public QueueWorkItem? PickupNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile) =>
        _allocation.PickupNextAvailableWorkItem(tenantId, userId, accessProfile);

    /// <inheritdoc cref="IProcessManager.GetQueueWorkItems"/>
    public QueueWorkListEnvelope GetQueueWorkItems(
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20) =>
        _queues.GetQueueWorkItems(tenantId, userId, accessProfile, statuses, sort, searchText, pageIndex, pageSize);

    /// <inheritdoc cref="IProcessManager.GetTeamWorkItems"/>
    public QueueWorkListEnvelope GetTeamWorkItems(
        string tenantId,
        string teamId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20) =>
        _queues.GetTeamWorkItems(tenantId, teamId, accessProfile, statuses, sort, searchText, pageIndex, pageSize);

    public IEnumerable<ServiceBlueprint> GetAllDefinitions() => _registry.All;

    public ServiceBlueprint? GetDefinition(string key) => _registry.Find(key);

    /// <summary>
    /// Registers or updates a definition in the live engine — an upsert, not update-only. A brand
    /// new key (one this engine has never seen, e.g. a blueprint an agent or human just authored
    /// from scratch via save_service_blueprint) must actually become servable here, or the documented
    /// promise that "a save reaches the live engine immediately" is false for exactly the scenario
    /// — authoring a new service — the whole toolkit exists for. Always returns true.
    /// </summary>
    public bool UpdateDefinition(string key, ServiceBlueprint updated)
    {
        _registry.Upsert(key, updated);
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
    public bool RemoveDefinition(string key) => _registry.Remove(key);

    public bool Reset(string instanceId)
    {
        if (!_instances.Remove(instanceId))
        {
            return false;
        }

        Logger.LogInformation("Reset (deleted) instance {Id}", instanceId);
        return true;
    }

    public void ResetAll()
    {
        _instances.Clear();
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

    /// <summary>
    /// The most recently computed <see cref="CalculationResult"/> for an instance, if its
    /// current stage has a calculations block and it evaluated cleanly — <c>null</c> if the
    /// instance doesn't exist, its stage has no calculations block, or evaluation failed. A
    /// composed caller (e.g. the simulation runner) uses this to read raw calculated values
    /// without duplicating evaluation itself.
    /// </summary>
    public CalculationResult? GetLastCalculationResult(string instanceId) =>
        _instances.TryGet(instanceId, out var instance) ? instance.LastCalculationResult : null;

    ServiceRequestResponseEnvelope IEnvelopeSource.BuildEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId) => BuildEnvelope(instance, definition, accessProfile, userId);

    protected ServiceRequestResponseEnvelope BuildEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId)
    {
        if (instance.IsAborted)
        {
            return Envelopes.Aborted(instance);
        }

        var workItems = _workItems.FindAccessibleWorkItems(instance, definition, accessProfile, userId);
        var visibleItem = workItems is [var firstItem, ..] ? firstItem : null;

        if (visibleItem is null)
        {
            return Envelopes.Error(
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
                    && _instances.TryGet(instance.InstanceId, out var refreshed))
                {
                    return BuildEnvelope(refreshed, definition, accessProfile, userId);
                }

                return Envelopes.JoinWaiting(instance, definition, joinGateway);
            }
        }

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == visibleItem.StageKey);
        if (stage == null)
        {
            return Envelopes.Error(
                $"State '{visibleItem.StageKey}' not found in definition '{definition.DefinitionKey}'.",
                "STATE_NOT_FOUND");
        }

        var renderData = BuildRenderData(instance, definition, stage);
        var calc = _calculations.EvaluateDefinitionCalculations(instance, definition, stage);
        if (calc is not null)
        {
            renderData ??= new JsonObject();
            renderData["live"] = StageCalculations.BuildLiveModel(definition, calc);
        }

        var components = _renderer.BuildComponents(stage.Components, instance.FieldValues, calc);
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
            RequestPolicy = definition.RequestPolicy,
            AllowManualRestart = definition.AllowManualRestart
        };
    }

    private ServiceRequestResponseEnvelope CreateAndRegisterNewInstance(
        string blueprintKey,
        string tenantId,
        string userId,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string? action,
        string reason)
    {
        var instance = CreateNewInstance(
            blueprintKey, tenantId, userId, definition.InitialStage, ResolveIsAuthenticated(tenantId, userId),
            accessProfile.ConcurrencyScopeKey ?? userId);
        if (InitializeNewInstance(instance, definition, action) is { } error)
        {
            return error;
        }

        instance = _instances.Save(instance, userId);

        Logger.LogInformation("Created service request {Id} for key={Key} ({Reason})", instance.InstanceId, blueprintKey, reason);
        return BuildEnvelope(instance, definition, accessProfile, userId);
    }

    private static ServiceRequest CreateNewInstance(
        string blueprintKey,
        string tenantId,
        string userId,
        string initialStage,
        bool isAuthenticated,
        string concurrencyScopeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServiceRequest
        {
            InstanceId = Guid.NewGuid().ToString(),
            BlueprintKey = blueprintKey,
            TenantId = tenantId,
            UserId = userId,
            ConcurrencyScopeKey = concurrencyScopeKey,
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

    protected ServiceRequestResponseEnvelope HandleSplitGatewayAdvance(
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
            var bulkDatasetUpdates = _bulkDatasets.ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, mergedFieldValues, cursor);
            if (bulkDatasetUpdates.Count > 0)
            {
                mergedFieldValues = Merge(mergedFieldValues, bulkDatasetUpdates);
            }

            newInvocations.AddRange(_supportSystems.ExecuteOnEnterSupportSystemActions(instance.InstanceId, definition, mergedFieldValues, cursor));
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

        updated = _instances.Save(updated, userId, AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: arrivingTransition.FromState, toStageKey: splitGateway.Key, action: arrivingTransition.Action,
            detail: $"split gateway fanned out to {newCursors.Count} cursors"));
        Logger.LogInformation(
            "Split gateway '{Gateway}': instance {Id} fanned out to {Count} cursors.",
            splitGateway.Key, instance.InstanceId, newCursors.Count);

        return BuildEnvelope(updated, definition, accessProfile, userId);
    }

    protected ServiceRequestResponseEnvelope HandleJoinGatewayAdvance(
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

            waitingInstance = _instances.Save(waitingInstance, userId, AuditEvents.Transition(
                instance.InstanceId, userId, cursorId: arrivingCursorId,
                fromStageKey: arrivingTransition.FromState, toStageKey: gatewayKey, action: arrivingTransition.Action,
                detail: $"arrived at join, waiting ({arrivedQueues.Count}/{requiredQueues.Count} queues)"));
            Logger.LogInformation(
                "Join gateway '{Gateway}': instance {Id} waiting ({Arrived}/{Required} queues).",
                gatewayKey, instance.InstanceId, arrivedQueues.Count, requiredQueues.Count);

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
            var owner = _instances.GetAll().FirstOrDefault(
                i => i.SupportSystemInvocations.Any(inv => inv.InvocationId == invocationId && !inv.Resolved));

            if (owner is null)
            {
                return Envelopes.Error(
                    $"No pending support-system invocation '{invocationId}' found.",
                    "SUPPORT_SYSTEM_INVOCATION_NOT_FOUND");
            }

            var invocation = owner.SupportSystemInvocations.First(inv => inv.InvocationId == invocationId);
            var capability = SupportSystemRegistry.FindCapability(invocation.SupportSystemKey, invocation.CapabilityKey);
            if (capability is null || capability.Outcomes.All(o => o.Key != outcomeKey))
            {
                return Envelopes.Error(
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

            _instances.Save(withResolvedInvocation, withResolvedInvocation.UserId);

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

        return Envelopes.Error(
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
            var bulkDatasetUpdates = _bulkDatasets.ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, releasedFieldValues, cursor);
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

        releasedInstance = _instances.Save(releasedInstance, userId, AuditEvents.Transition(
            instance.InstanceId, userId, cursorId: null,
            fromStageKey: gatewayKey, toStageKey: selectedOutgoing[0].ToState, action: selectedOutgoing[0].Action,
            detail: "join released"));
        return BuildEnvelope(releasedInstance, definition, accessProfile, userId);
    }

    private static IReadOnlyList<RequestCursor> MoveCursor(
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

    private static string? FirstActiveStageCursorKey(IReadOnlyList<RequestCursor> cursors) =>
        cursors.FirstOrDefault(c => !c.IsAtGateway)?.CurrentNodeKey;

    /// <summary>
    /// "Terminal" from <paramref name="accessProfile"/>'s own point of view — deliberately not a
    /// blind read of <see cref="ServiceRequest.CurrentStage"/>, which is a single field
    /// covering every cursor a multi-queue instance has (see its own remarks:
    /// "reflects the first active stage cursor", not any *particular* one). A caseworker's cursor
    /// waiting at a join gateway is never terminal, no matter what some other queue's cursor
    /// (e.g. an automation-queue "please wait" stage, itself rendered as a bare panel — the exact
    /// same shape a genuine confirmation stage uses) happens to be sitting on. Found live: an
    /// in-progress njf-contributions submission was misclassified as terminal purely because its
    /// automation-queue cursor had already reached such a stage while the caseworker's own cursor
    /// was still waiting at the join — reuses the same actor-relative resolution
    /// <see cref="BuildEnvelope"/> already gets right via <see cref="FindAccessibleWorkItems"/>,
    /// rather than the older, simpler check this replaced.
    /// </summary>
    private bool IsTerminalInstance(ServiceRequest instance, ServiceBlueprint definition, ActorProfile accessProfile)
    {
        if (instance.IsAborted)
        {
            return true;
        }

        var visibleItem = _workItems.FindAccessibleWorkItems(instance, definition, accessProfile) is [var firstItem, ..] ? firstItem : null;
        return visibleItem is not null && visibleItem.IsTerminal(definition);
    }

    /// <summary>
    /// Groups "is there already one?" by <paramref name="accessProfile"/>'s own
    /// <see cref="ActorProfile.ConcurrencyScopeKey"/> when set, falling back to
    /// <paramref name="userId"/> otherwise — matching how each candidate instance's own
    /// <see cref="ServiceRequest.ConcurrencyScopeKey"/> was resolved at creation time
    /// (<see cref="CreateAndRegisterNewInstance"/>), so this stays exactly today's per-user
    /// behaviour for every caller that never sets a scope key.
    /// </summary>
    private ServiceRequest? FindLatestInstance(string tenantId, string userId, string blueprintKey, ActorProfile accessProfile)
    {
        var scopeKey = accessProfile.ConcurrencyScopeKey ?? userId;
        return _instances.GetAll()
            .Where(instance =>
                string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(instance.ConcurrencyScopeKey, scopeKey, StringComparison.Ordinal)
                && string.Equals(instance.BlueprintKey, blueprintKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.UpdatedAt)
            .ThenByDescending(instance => instance.CreatedAt)
            .FirstOrDefault();
    }

}
