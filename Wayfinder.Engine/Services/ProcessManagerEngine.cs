using System.Text.Json.Nodes;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;
using Microsoft.Extensions.Logging;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Stores;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Generic in-memory runtime engine that executes Wayfinder service blueprints.
/// </summary>
public partial class ProcessManagerEngine : IProcessManager
{
    private readonly ILogger _logger;
    private readonly Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?>? _serviceInputsResolver;
    private readonly BlueprintRegistry _registry;
    private readonly InstanceRepository _instances;
    private readonly SupportSystemOutcomes _outcomes;
    private readonly InstanceEntry _entry;
    private readonly InstanceAdvancer _advancer;
    private readonly ServiceFieldSync _sync;
    private readonly InstanceAdmin _admin;
    private readonly WorkQueues _queues;
    private readonly WorkAllocation _allocation;

    /// <summary>
    /// The engine is a thin front door: the constructor wires the collaborators that each own one concern
    /// (entry, advancing, gateways, work allocation, rendering, support systems, bulk data) and every public
    /// method hands off to one of them. A host customises it by overriding <see cref="ResolveServiceInputs"/>,
    /// <see cref="ResolveIsAuthenticated"/> and <see cref="BuildRenderData"/>.
    /// </summary>
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
        _logger = logger;
        _serviceInputsResolver = serviceInputsResolver;

        var clients = (supportSystemClients ?? [])
            .ToDictionary(client => client.SupportSystemKey, StringComparer.Ordinal);
        var concurrencyPolicies = (requestConcurrencyPolicies ?? [])
            .SelectMany(policy => policy.DefinitionKeys.Select(key => (key, policy)))
            .ToDictionary(pair => pair.key, pair => pair.policy, StringComparer.OrdinalIgnoreCase);

        _registry = new BlueprintRegistry(definitionStore, logger);
        _instances = new InstanceRepository(
            instanceStore ?? new InMemoryServiceRequestStore(),
            auditLogStore ?? new InMemoryAuditLogStore(),
            _registry);

        var calculations = new StageCalculations(logger, _instances, ResolveServiceInputs);
        var workItems = new WorkItemFinder(calculations);
        var renderer = new StageRenderer(sanitizer, calculations, logger);
        _outcomes = new SupportSystemOutcomes(_instances, clients, this, logger);
        var envelopes = new EnvelopeBuilder(
            _instances, workItems, calculations, renderer, BuildRenderData, _outcomes.TryPollResolveSupportSystemInvocations);
        var bulkDatasets = new BulkDatasetActions(bulkDatasetStore, logger);
        var supportSystems = new SupportSystemActions(clients, logger);
        var gateways = new GatewayAdvancer(_instances, bulkDatasets, supportSystems, envelopes, logger);

        _entry = new InstanceEntry(_registry, _instances, workItems, envelopes, concurrencyPolicies, ResolveIsAuthenticated, logger);
        _advancer = new InstanceAdvancer(
            _registry, _instances, workItems, calculations, renderer, envelopes, gateways, supportSystems, logger);
        _sync = new ServiceFieldSync(_registry, _instances, envelopes, _entry, bulkDatasetStore);
        _admin = new InstanceAdmin(_instances, _registry, logger);
        _allocation = new WorkAllocation(_instances, _registry, workItems, envelopes);
        _queues = new WorkQueues(_instances, _registry, workItems, _outcomes.TryPollResolveSupportSystemInvocations);
    }

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

    /// <inheritdoc cref="IProcessManager.SyncServiceFields"/>
    public ServiceRequestResponseEnvelope SyncServiceFields(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile,
        Dictionary<string, object?> updates) =>
        _sync.SyncServiceFields(instanceId, tenantId, userId, accessProfile, updates);

    /// <inheritdoc cref="IProcessManager.SyncBulkDatasetSyncState"/>
    public ServiceRequestResponseEnvelope SyncBulkDatasetSyncState(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile, string datasetId) =>
        _sync.SyncBulkDatasetSyncState(instanceId, tenantId, userId, accessProfile, datasetId);

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

        InstanceReset(_logger, instanceId);
        return true;
    }

    public void ResetAll()
    {
        _instances.Clear();
        AllInstancesReset(_logger);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Reset (deleted) instance {Id}")]
    private static partial void InstanceReset(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "ResetAll: all service requests cleared")]
    private static partial void AllInstancesReset(ILogger logger);
}
