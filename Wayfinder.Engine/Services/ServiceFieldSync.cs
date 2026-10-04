using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.FieldValueMerge;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>Host-driven writes to an instance's <c>source: "service"</c> fields, and the bulk-dataset dirty count that rides on them.</summary>
internal sealed class ServiceFieldSync(
    BlueprintRegistry registry,
    InstanceRepository instances,
    EnvelopeBuilder envelopes,
    InstanceEntry entry,
    IBulkDatasetStore? bulkDatasetStore)
{
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
            if (!instances.TryGet(instanceId, out var instance))
            {
                return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
            }

            if (!CanAccessInstance(instance, tenantId, userId, accessProfile))
            {
                return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
            }

            if (!registry.TryGet(instance.BlueprintKey, out var definition))
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

            var savedSync = instances.TrySaveIfVersionMatches(updatedInstance, userId, instance.StateVersion, auditEvent: null);
            if (savedSync is not null)
            {
                return envelopes.BuildEnvelope(savedSync, definition, accessProfile, userId);
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
        if (!instances.TryGet(instanceId, out var instance))
        {
            return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
        }

        if (!registry.TryGet(instance.BlueprintKey, out var definition))
        {
            return Envelopes.Error($"Blueprint '{instance.BlueprintKey}' not found.", "DEFINITION_NOT_FOUND");
        }

        var dirtyCountField = BulkDatasetActions.FindDeclaringIngestAction(definition, instance.FieldValues, datasetId)
            ?.Parameters["dirtyCountField"]?.GetValue<string>();

        if (bulkDatasetStore is null || string.IsNullOrWhiteSpace(dirtyCountField))
        {
            // Not opted in for this blueprint/dataset — same "declared-but-unused count field is a
            // no-op" convention errorCountField/warningCountField/acceptedCountField already follow.
            return entry.GetCurrent(instance.BlueprintKey, tenantId, userId, accessProfile, instanceId);
        }

        var summary = bulkDatasetStore.GetSummaryAsync(instanceId, datasetId).GetAwaiter().GetResult();
        if (summary is null)
        {
            return entry.GetCurrent(instance.BlueprintKey, tenantId, userId, accessProfile, instanceId);
        }

        return SyncServiceFields(
            instanceId, tenantId, userId, accessProfile,
            new Dictionary<string, object?> { [dirtyCountField] = (decimal)summary.DirtyRowCount });
    }
}
