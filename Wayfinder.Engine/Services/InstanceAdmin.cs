using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Models;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>Instance-level administration and listing: admin search, abort, per-user lists and claiming an anonymous visitor's instances on sign-in.</summary>
internal sealed partial class InstanceAdmin(InstanceRepository instances, BlueprintRegistry registry, ILogger logger)
{
    public ServiceRequestAdminListEnvelope SearchInstancesForAdmin(ServiceRequestAdminQuery query)
    {
        query ??= new ServiceRequestAdminQuery();
        var effectivePageIndex = Math.Max(query.PageIndex, 0);
        var effectivePageSize = Math.Clamp(query.PageSize, 1, 100);

        var matched = instances.GetAll()
            .Where(instance => query.IncludeAborted || !instance.IsAborted)
            .Where(instance => query.BlueprintKey is null
                || string.Equals(instance.BlueprintKey, query.BlueprintKey, StringComparison.Ordinal))
            .Where(instance => query.TenantId is null
                || string.Equals(instance.TenantId, query.TenantId, StringComparison.Ordinal))
            .Select(instance => ToAdminSummary(instance))
            .Where(summary => MatchesAdminSearch(summary, query.SearchText))
            .ToArray();

        var ordered = ApplyAdminSort(matched, query.Sort);
        var page = ordered.Skip(effectivePageIndex * effectivePageSize).Take(effectivePageSize).ToArray();

        return new ServiceRequestAdminListEnvelope
        {
            Items = page,
            PageIndex = effectivePageIndex,
            PageSize = effectivePageSize,
            TotalMatchingCount = ordered.Length
        };
    }

    private ServiceRequestAdminSummary ToAdminSummary(ServiceRequest instance)
    {
        registry.TryGet(instance.BlueprintKey, out var definition);
        var stage = definition?.Stages.FirstOrDefault(s => s.StageKey == instance.CurrentStage);
        var stepType = stage?.Components.InferStepType() ?? "question";

        return new ServiceRequestAdminSummary
        {
            InstanceId = instance.InstanceId,
            BlueprintKey = instance.BlueprintKey,
            BlueprintDisplayName = definition?.DisplayName ?? instance.BlueprintKey,
            TenantId = instance.TenantId,
            UserId = instance.UserId,
            CurrentStage = instance.CurrentStage,
            CurrentStateDisplayName = stage?.DisplayName ?? instance.CurrentStage,
            IsCompleted = stepType == "confirmation",
            IsAborted = instance.IsAborted,
            AbortedAt = instance.AbortedAt,
            AbortedReason = instance.AbortedReason,
            AbortedByUserId = instance.AbortedByUserId,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = instance.UpdatedAt
        };
    }

    private static bool MatchesAdminSearch(ServiceRequestAdminSummary summary, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        bool Contains(string? haystack) =>
            !string.IsNullOrEmpty(haystack) && haystack.Contains(searchText, StringComparison.OrdinalIgnoreCase);

        return Contains(summary.InstanceId)
            || Contains(summary.BlueprintDisplayName)
            || Contains(summary.CurrentStateDisplayName)
            || Contains(summary.UserId);
    }

    private static ServiceRequestAdminSummary[] ApplyAdminSort(
        IReadOnlyList<ServiceRequestAdminSummary> items, ServiceRequestAdminSort sort) => sort switch
    {
        ServiceRequestAdminSort.UpdatedAtNewestFirst => items
            .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        ServiceRequestAdminSort.CreatedAtOldestFirst => items
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        ServiceRequestAdminSort.CreatedAtNewestFirst => items
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
        _ => items
            .OrderBy(i => i.UpdatedAt).ThenBy(i => i.InstanceId, StringComparer.Ordinal).ToArray(),
    };

    public bool AbortInstance(string instanceId, string reason, string abortedByUserId)
    {
        if (!instances.TryGet(instanceId, out var instance))
        {
            return false;
        }

        if (instance.IsAborted)
        {
            return true;
        }

        var aborted = instance with
        {
            IsAborted = true,
            AbortedAt = DateTimeOffset.UtcNow,
            AbortedReason = reason,
            AbortedByUserId = abortedByUserId,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        instances.SaveAsIs(aborted);
        Aborted(logger, instanceId, abortedByUserId, reason);
        return true;
    }

    public ServiceRequestListEnvelope GetInstances(string tenantId, string userId)
    {
        var userInstances = instances.GetAll()
            .Where(i => string.Equals(i.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(i.UserId, userId, StringComparison.Ordinal))
            .Select(instance =>
            {
                registry.TryGet(instance.BlueprintKey, out var definition);
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

        var allInstances = instances.GetAll().ToList();
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

            // ConcurrencyScopeKey only ever moves with the re-key when it was still the implicit
            // default (equal to fromUserId, set that way at creation — see CreateNewInstance).
            // An explicit host-chosen scope (e.g. an organisation key) isn't something "this one
            // anonymous browser session later signed in" should silently reassign.
            var concurrencyScopeKey = string.Equals(instance.ConcurrencyScopeKey, fromUserId, StringComparison.Ordinal)
                ? toUserId
                : instance.ConcurrencyScopeKey;

            instances.Save(instance with
            {
                UserId = toUserId,
                ConcurrencyScopeKey = concurrencyScopeKey,
                IsAuthenticated = true,
                UpdatedAt = DateTimeOffset.UtcNow
            }, toUserId);
            claimed.Add(instance.InstanceId);
        }

        return claimed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Instance {Id} aborted by {AbortedBy}: {Reason}")]
    private static partial void Aborted(ILogger logger, string id, string abortedBy, string reason);
}
