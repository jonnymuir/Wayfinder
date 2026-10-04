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
    private readonly InstanceAdvancer _advancer;
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
        _advancer = new InstanceAdvancer(
            _registry, _instances, _workItems, _calculations, _renderer, _envelopes, _gateways, _supportSystems, logger);
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

    public ServiceRequestResponseEnvelope Advance(
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

    public ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues) =>
        _advancer.Advance(instanceId, tenantId, userId, accessProfile, action, expectedStateVersion, fieldValues);

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
