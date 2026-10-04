using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using static Wayfinder.Engine.Services.FieldValueMerge;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Settles support-system calls: by polling the client, or when an external system reports its outcome
/// (a webhook). Either way the waiting automation cursor advances through <paramref name="advancer"/> as
/// if its own actor had submitted the outcome.
/// </summary>
internal sealed partial class SupportSystemOutcomes(
    InstanceRepository instances,
    IReadOnlyDictionary<string, ISupportSystemClient> clients,
    IProcessManager advancer,
    ILogger logger)
{
    /// <summary>
    /// Gives any support-system invocation still blocking <paramref name="joinGateway"/> a chance
    /// to resolve via poll, the generic counterpart to the webhook receiver resolving one
    /// asynchronously — called every time a client re-polls a waiting join gateway (see
    /// <c>BuildEnvelope</c>). Only checks invocations whose capability actually declared
    /// <see cref="SupportSystemCompletionMode.Poll"/> support; a webhook-only capability is never
    /// polled, it can only resolve via <see cref="ResolveSupportSystemOutcome"/>. Returns true if
    /// at least one invocation resolved (and therefore state has already been saved, possibly
    /// including a full join release) — the caller should re-derive its response from a fresh
    /// read rather than the <paramref name="instance"/> it started with.
    /// </summary>
    public bool TryPollResolveSupportSystemInvocations(
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
                || !clients.TryGetValue(invocation.SupportSystemKey, out var client))
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
                StatusCheckFailed(logger, ex, invocation.SupportSystemKey, invocation.CapabilityKey, invocation.InvocationId);
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
    /// <c>Advance</c>
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
            var owner = instances.GetAll().FirstOrDefault(
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

            instances.Save(withResolvedInvocation, withResolvedInvocation.UserId);

            var advanced = advancer.Advance(
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Support system '{System}' capability '{Capability}' status check failed for invocation '{Invocation}'.")]
    private static partial void StatusCheckFailed(ILogger logger, Exception exception, string system, string capability, string invocation);
}
