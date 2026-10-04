using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.QueueAccess;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Decides which instance a visit lands on: the one named, the latest one, or a fresh one — according to the
/// blueprint's request policy (<c>single</c>, <c>multiple</c>, <c>prompt</c>) or a host-registered
/// <see cref="IRequestConcurrencyPolicy"/>, and the explicit <c>start-new</c> / <c>resume</c> actions.
/// </summary>
internal sealed partial class InstanceEntry(
    BlueprintRegistry registry,
    InstanceRepository instances,
    WorkItemFinder workItems,
    EnvelopeBuilder envelopes,
    IReadOnlyDictionary<string, IRequestConcurrencyPolicy> concurrencyPolicies,
    Func<string, string, bool> resolveIsAuthenticated,
    ILogger logger)
{
    /// <summary>Who is asking, for which blueprint.</summary>
    private readonly record struct Visit(
        string BlueprintKey, string TenantId, string UserId, ActorProfile AccessProfile, ServiceBlueprint Definition);

    public ServiceRequestResponseEnvelope GetCurrent(
        string blueprintKey,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string? instanceId = null,
        string? action = null)
    {
        if (!registry.TryGet(blueprintKey, out var definition))
        {
            BlueprintNotFound(logger, blueprintKey);
            return Envelopes.Error(
                $"Blueprint '{blueprintKey}' is not registered with this application.",
                "DEFINITION_NOT_FOUND");
        }

        var visit = new Visit(blueprintKey, tenantId, userId, accessProfile, definition);

        if (!string.IsNullOrEmpty(instanceId))
        {
            return ResumeSpecific(visit, instanceId);
        }

        var existing = FindLatestInstance(visit);

        if (!CanStartInitialState(definition, accessProfile))
        {
            return Envelopes.Error("Access denied to start this queue.", "ACCESS_DENIED");
        }

        if (string.Equals(action, "start-new", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAndRegister(visit, "action=start-new");
        }

        if (string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
        {
            return existing is not null
                ? Resume(visit, existing, "action=resume")
                : CreateAndRegister(visit, "action=resume, no existing");
        }

        // A host-registered custom policy takes over entirely for the blueprints it names; every other
        // blueprint falls straight through to the built-in policies. Explicit start-new/resume already
        // returned above regardless of policy.
        return concurrencyPolicies.TryGetValue(blueprintKey, out var customPolicy)
            ? ApplyCustomPolicy(visit, customPolicy)
            : ApplyRequestPolicy(visit, existing);
    }

    /// <summary>
    /// The "start" affordance a genuine ambient <see cref="GetCurrent"/> (a "continue where I left off" link)
    /// deliberately isn't: an ordinary visit must keep showing a terminal instance forever under "single"
    /// (a returning citizen sees "Thank you", not a silently-reset blank form), but a distinct "start a new
    /// one" link shouldn't hand back a stale confirmation from months ago either. A non-terminal existing
    /// instance is reinstated exactly as ambient <c>GetCurrent</c> does — never abandons in-progress work;
    /// only a genuinely terminal (or absent) existing instance triggers a real fresh one.
    /// </summary>
    public ServiceRequestResponseEnvelope GetCurrentOrStartFresh(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile)
    {
        var existing = FindLatestInstance(blueprintKey, tenantId, userId, accessProfile);
        if (existing is not null
            && registry.TryGet(blueprintKey, out var definition)
            && IsTerminal(existing, definition, accessProfile))
        {
            return GetCurrent(blueprintKey, tenantId, userId, accessProfile, action: "start-new");
        }

        return GetCurrent(blueprintKey, tenantId, userId, accessProfile);
    }

    /// <summary>
    /// The gated entry point a citizen-facing surface must use for an untrusted <c>action: "start-new"</c>
    /// request (e.g. a "Start again" link's query string, or the "prompt" policy's instance picker) —
    /// refuses it outright unless <see cref="ServiceBlueprint.AllowManualRestart"/> is set, falling back to
    /// plain ambient <c>GetCurrent</c> (never an error: a disallowed or stale link must not break the page,
    /// it must just not do anything special).
    /// </summary>
    public ServiceRequestResponseEnvelope GetCurrentOrManualRestart(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile)
    {
        if (!registry.TryGet(blueprintKey, out var definition) || !definition.AllowManualRestart)
        {
            ManualRestartNotAllowed(logger, blueprintKey);
            return GetCurrent(blueprintKey, tenantId, userId, accessProfile);
        }

        return GetCurrent(blueprintKey, tenantId, userId, accessProfile, action: "start-new");
    }

    private ServiceRequestResponseEnvelope ResumeSpecific(Visit visit, string instanceId)
    {
        if (!instances.TryGet(instanceId, out var specific))
        {
            return Envelopes.Error($"Service request '{instanceId}' not found.", "INSTANCE_NOT_FOUND");
        }

        if (!CanAccessInstance(specific, visit.TenantId, visit.UserId, visit.AccessProfile))
        {
            return Envelopes.Error("Access denied to this service request.", "ACCESS_DENIED");
        }

        ResumingSpecific(logger, instanceId);
        return envelopes.BuildEnvelope(specific, visit.Definition, visit.AccessProfile, visit.UserId);
    }

    private ServiceRequestResponseEnvelope Resume(Visit visit, ServiceRequest existing, string reason)
    {
        ResumingExisting(logger, existing.InstanceId, reason);
        return envelopes.BuildEnvelope(existing, visit.Definition, visit.AccessProfile, visit.UserId);
    }

    private ServiceRequestResponseEnvelope ApplyCustomPolicy(Visit visit, IRequestConcurrencyPolicy policy)
    {
        var candidates = instances.GetAll()
            .Where(instance =>
                string.Equals(instance.TenantId, visit.TenantId, StringComparison.Ordinal)
                && string.Equals(instance.BlueprintKey, visit.BlueprintKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var decision = policy
            .EvaluateAsync(visit.Definition, visit.TenantId, visit.UserId, visit.AccessProfile, candidates)
            .GetAwaiter().GetResult();

        switch (decision.Outcome)
        {
            case RequestConcurrencyOutcome.ReuseExisting:
                return envelopes.BuildEnvelope(
                    decision.ExistingInstance ?? throw new InvalidOperationException(
                        $"{policy.GetType().Name} returned ReuseExisting with no ExistingInstance."),
                    visit.Definition, visit.AccessProfile, visit.UserId);
            case RequestConcurrencyOutcome.Deny:
                return Envelopes.Error(
                    decision.DenyReason ?? "This request was denied by a registered concurrency policy.",
                    "CONCURRENCY_POLICY_DENIED");
            case RequestConcurrencyOutcome.AllowNew:
            default:
                return CreateAndRegister(visit, "custom concurrency policy: AllowNew");
        }
    }

    private ServiceRequestResponseEnvelope ApplyRequestPolicy(Visit visit, ServiceRequest? existing)
    {
        var policy = visit.Definition.RequestPolicy;

        if (string.Equals(policy, "multiple", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAndRegister(visit, "policy=multiple");
        }

        if (string.Equals(policy, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            return existing is not null && !IsTerminal(existing, visit.Definition, visit.AccessProfile)
                ? InstancePicker(visit, existing)
                : CreateAndRegister(visit, "policy=prompt, no active");
        }

        // "single" means at most one instance per user for this blueprint, full stop — once it reaches a
        // terminal stage it keeps being shown on every subsequent visit (a member returning to the page sees
        // "Thank you", not a silently-reset blank form). The PRG redirect after a POST relies on this same
        // fallthrough to show the confirmation page for the visit that just submitted it.
        return existing is null
            ? CreateAndRegister(visit, "no existing instance")
            : envelopes.BuildEnvelope(existing, visit.Definition, visit.AccessProfile, visit.UserId);
    }

    private ServiceRequestResponseEnvelope InstancePicker(Visit visit, ServiceRequest existing)
    {
        ReturningInstancePicker(logger, existing.InstanceId, visit.BlueprintKey);

        var currentStage = visit.Definition.Stages.FirstOrDefault(s => s.StageKey == existing.CurrentStage);
        return new ServiceRequestResponseEnvelope
        {
            InstanceId = existing.InstanceId,
            ResponseState = "instance_picker",
            StateVersion = existing.StateVersion,
            CorrelationId = existing.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            RequestPolicy = "prompt",
            AllowManualRestart = visit.Definition.AllowManualRestart,
            Render = new StepContent
            {
                StepType = currentStage?.Components.InferStepType() ?? "question",
                StateDisplayName = currentStage?.DisplayName ?? visit.Definition.DisplayName,
                Components = Array.Empty<ComponentRenderPayload>(),
                AvailableActions = Array.Empty<ServiceRequestAction>()
            }
        };
    }

    private ServiceRequestResponseEnvelope CreateAndRegister(Visit visit, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var instance = new ServiceRequest
        {
            InstanceId = Guid.NewGuid().ToString(),
            BlueprintKey = visit.BlueprintKey,
            TenantId = visit.TenantId,
            UserId = visit.UserId,
            ConcurrencyScopeKey = visit.AccessProfile.ConcurrencyScopeKey ?? visit.UserId,
            IsAuthenticated = resolveIsAuthenticated(visit.TenantId, visit.UserId),
            CurrentStage = visit.Definition.InitialStage,
            StateVersion = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        instance = instances.Save(instance, visit.UserId);

        Created(logger, instance.InstanceId, visit.BlueprintKey, reason);
        return envelopes.BuildEnvelope(instance, visit.Definition, visit.AccessProfile, visit.UserId);
    }

    public bool IsTerminal(ServiceRequest instance, ServiceBlueprint definition, ActorProfile accessProfile)
    {
        if (instance.IsAborted)
        {
            return true;
        }

        var visibleItem = workItems.FindActorWorkItems(instance, definition, accessProfile) is [var firstItem, ..] ? firstItem : null;
        return visibleItem is not null && visibleItem.IsTerminal(definition);
    }

    private ServiceRequest? FindLatestInstance(Visit visit) =>
        FindLatestInstance(visit.BlueprintKey, visit.TenantId, visit.UserId, visit.AccessProfile);

    /// <summary>
    /// Groups "is there already one?" by the access profile's own <see cref="ActorProfile.ConcurrencyScopeKey"/>
    /// when set, falling back to the user id otherwise — matching how each instance's own scope key was
    /// resolved when it was created.
    /// </summary>
    private ServiceRequest? FindLatestInstance(string blueprintKey, string tenantId, string userId, ActorProfile accessProfile)
    {
        var scopeKey = accessProfile.ConcurrencyScopeKey ?? userId;
        return instances.GetAll()
            .Where(instance =>
                string.Equals(instance.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(instance.ConcurrencyScopeKey, scopeKey, StringComparison.Ordinal)
                && string.Equals(instance.BlueprintKey, blueprintKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.UpdatedAt)
            .ThenByDescending(instance => instance.CreatedAt)
            .FirstOrDefault();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service blueprint not found: {Key}")]
    private static partial void BlueprintNotFound(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming specific instance {Id}")]
    private static partial void ResumingSpecific(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming existing instance {Id} ({Reason})")]
    private static partial void ResumingExisting(ILogger logger, string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Active instance {Id} exists for key={Key}; returning instance_picker")]
    private static partial void ReturningInstancePicker(ILogger logger, string id, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created service request {Id} for key={Key} ({Reason})")]
    private static partial void Created(ILogger logger, string id, string key, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Manual restart (action=start-new) requested for blueprint '{Key}', which does not declare allowManualRestart — ignoring and resuming ambient state instead.")]
    private static partial void ManualRestartNotAllowed(ILogger logger, string key);
}
