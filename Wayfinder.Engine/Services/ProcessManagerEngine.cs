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
public class ProcessManagerEngine : IProcessManager
{
    private readonly IServiceContentSanitizer _sanitizer;
    private readonly BlueprintRegistry _registry;
    private readonly InstanceRepository _instances;
    private readonly StageCalculations _calculations;
    private readonly WorkItemFinder _workItems;
    private readonly StageRenderer _renderer;
    private readonly EnvelopeBuilder _envelopes;
    private readonly BulkDatasetActions _bulkDatasets;
    private readonly SupportSystemActions _supportSystems;
    private readonly InstanceAdmin _admin;
    private readonly WorkQueues _queues;
    private readonly WorkAllocation _allocation;
    private readonly GatewayAdvancer _gateways;
    private readonly SupportSystemOutcomes _outcomes;
    private readonly InstanceEntry _entry;
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
        _outcomes = new SupportSystemOutcomes(_instances, _supportSystemClients, this, logger);
        _envelopes = new EnvelopeBuilder(
            _instances, _workItems, _calculations, _renderer, BuildRenderData,
            _outcomes.TryPollResolveSupportSystemInvocations);
        _entry = new InstanceEntry(
            _registry, _instances, _workItems, _envelopes, _requestConcurrencyPolicies,
            ResolveIsAuthenticated, logger);
        _bulkDatasets = new BulkDatasetActions(bulkDatasetStore, logger);
        _supportSystems = new SupportSystemActions(_supportSystemClients, logger);
        _admin = new InstanceAdmin(_instances, _registry, logger);
        _allocation = new WorkAllocation(_instances, _registry, _workItems, _envelopes);
        _gateways = new GatewayAdvancer(_instances, _bulkDatasets, _supportSystems, _envelopes, logger);
        _queues = new WorkQueues(_instances, _registry, _workItems, _outcomes.TryPollResolveSupportSystemInvocations);
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
        string? action = null) =>
        _entry.GetCurrent(blueprintKey, tenantId, userId, accessProfile, instanceId, action);

    public ServiceRequestResponseEnvelope GetCurrentOrStartFresh(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile) =>
        _entry.GetCurrentOrStartFresh(blueprintKey, tenantId, userId, accessProfile);

    public ServiceRequestResponseEnvelope GetCurrentOrManualRestart(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile) =>
        _entry.GetCurrentOrManualRestart(blueprintKey, tenantId, userId, accessProfile);

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
            return _envelopes.BuildEnvelope(savedJumped, definition, accessProfile, userId);
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
                return _envelopes.BuildEnvelope(previewInstance, definition, accessProfile, userId) with { Problems = problems };
            }

            // Declarative cross-field business rules (StageDefinition.Validations) — the
            // checked once
            // field-level validation has already passed. Evaluated on the same merge of
            // persisted + just-submitted values FieldValueValidator above just accepted, never
            // on stale persisted data or on anything the client could claim was pre-checked.
            var stageValidationProblems = _calculations.EvaluateStageValidations(instance, definition, currentStage, fieldValues, action);
            if (stageValidationProblems.Count > 0)
            {
                var previewInstance = instance with { FieldValues = Merge(instance.FieldValues, fieldValues) };
                return _envelopes.BuildEnvelope(previewInstance, definition, accessProfile, userId) with { Problems = stageValidationProblems };
            }
        }

        // Check if the target is a gateway rather than a plain stage.
        var nextGateway = FindGateway(definition, transition.ToState);
        if (nextGateway != null)
        {
            return nextGateway.GatewayType == GatewayKind.Split
                ? _gateways.HandleSplitGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId)
                : _gateways.HandleJoinGatewayAdvance(instance, definition, transition, nextGateway, fieldValues, accessProfile, userId);
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
            var updatedCursors = GatewayAdvancer.MoveCursor(
                instance.Cursors, sourceCursor?.CursorId, transition.ToState, isAtGateway: false,
                newQueueKey: GetQueueKey(targetStage));
            var primaryStage = GatewayAdvancer.FirstActiveStageCursorKey(updatedCursors) ?? transition.ToState;
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
            return _envelopes.BuildEnvelope(savedMulti, definition, accessProfile, userId);
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

        return _envelopes.BuildEnvelope(savedUpdated, definition, accessProfile, userId);
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
                return _envelopes.BuildEnvelope(savedSync, definition, accessProfile, userId);
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

    /// <summary>
    /// Settles a support-system invocation with the outcome an external system reported (the webhook receiver
    /// and the poll check both call this), advancing the cursor that was waiting on it.
    /// </summary>
    public ServiceRequestResponseEnvelope ResolveSupportSystemOutcome(
        string invocationId,
        string outcomeKey,
        JsonObject? resultPayload = null) =>
        _outcomes.ResolveSupportSystemOutcome(invocationId, outcomeKey, resultPayload);

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

    /// <summary>
    /// Whether <paramref name="userId"/> identifies a signed-in user, for a store that wants
    /// to apply a different retention policy for authenticated vs anonymous instances (see
    /// <see cref="ServiceRequest.IsAuthenticated"/>). The base engine has no identity
    /// model of its own — always false — a host overrides this using whatever identity
    /// resolution its own request pipeline already performs.
    /// </summary>
    protected virtual bool ResolveIsAuthenticated(string tenantId, string userId) => false;

}
