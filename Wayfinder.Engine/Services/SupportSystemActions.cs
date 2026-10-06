using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Starts a stage's <c>onEnter</c> <c>support-system-call</c> actions through the registered
/// <see cref="ISupportSystemClient"/>s. See docs/guides/support-systems.md. A misconfigured action or a
/// client that throws is skipped (and logged) rather than failing the request. A call that cannot be
/// dispatched follows the action's <see cref="SupportCallFailurePolicy"/>: by default the whole
/// advance is abandoned (<see cref="SupportCallBatch.Failed"/>) so the visitor can try again.
/// </summary>
internal sealed partial class SupportSystemActions(IReadOnlyDictionary<string, ISupportSystemClient> clients, ILogger logger)
{
    /// <summary>
    /// Runs every <c>onEnter</c> <c>support-system-call</c> action declared on the stage a cursor
    /// just landed on, recording a <see cref="SupportSystemInvocation"/> for each successful
    /// start. Only wired into the multi-cursor paths (a support-system call only makes sense
    /// against a genuinely separate automation-queue cursor, per docs/guides/support-systems.md)
    /// — a single-queue blueprint has no automation actor for such an action to belong to, so
    /// this deliberately isn't called from the single-cursor "regular stage transition" path.
    /// </summary>
    public SupportCallBatch ExecuteOnEnterSupportSystemActions(
        string instanceId,
        ServiceBlueprint definition,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor? cursor)
    {
        var calls = cursor is null
            ? []
            : definition.Stages.FirstOrDefault(s => s.StageKey == cursor.CurrentNodeKey)?.Actions?.Where(IsOnEnterSupportSystemCall) ?? [];

        var invocations = new List<SupportSystemInvocation>();
        foreach (var action in calls)
        {
            var (invocation, failed) = TryExecuteSupportSystemCall(instanceId, fieldValues, cursor!, action);
            if (failed)
            {
                return new SupportCallBatch(invocations, Failed: true);
            }

            if (invocation is not null)
            {
                invocations.Add(invocation);
            }
        }

        return new SupportCallBatch(invocations, Failed: false);
    }

    private static Dictionary<string, SupportSystemInputValue> ResolveInputs(
        SupportSystemCapabilityDescriptor capability,
        ActionDefinition action,
        IReadOnlyDictionary<string, object?> fieldValues)
    {
        var inputFieldRefs = action.Parameters["inputs"]?.AsObject();
        var inputs = new Dictionary<string, SupportSystemInputValue>(StringComparer.Ordinal);
        foreach (var input in capability.Inputs)
        {
            var fieldKey = inputFieldRefs?[input.Key]?.GetValue<string>();
            var raw = fieldKey is not null ? fieldValues.GetValueOrDefault(fieldKey) : null;
            inputs[input.Key] = SupportSystemInputValue.Resolve(raw);
        }

        return inputs;
    }

    private static bool IsOnEnterSupportSystemCall(ActionDefinition action) =>
        action.Timing == ActionTiming.OnEnter
        && string.Equals(action.Type, SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal);

    private (SupportSystemInvocation? Invocation, bool Failed) TryExecuteSupportSystemCall(
        string instanceId,
        IReadOnlyDictionary<string, object?> fieldValues,
        RequestCursor cursor,
        ActionDefinition action)
    {
        var supportSystemKey = action.Parameters["supportSystemKey"]?.GetValue<string>();
        var capabilityKey = action.Parameters["capabilityKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(supportSystemKey) || string.IsNullOrWhiteSpace(capabilityKey))
        {
            MissingKeys(logger, cursor.CurrentNodeKey);
            return (null, false);
        }

        var capability = SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey);
        if (capability is null)
        {
            UnregisteredCapability(logger, cursor.CurrentNodeKey, supportSystemKey, capabilityKey);
            return (null, false);
        }

        if (!clients.TryGetValue(supportSystemKey, out var client))
        {
            NoClient(logger, supportSystemKey);
            return (null, false);
        }

        var inputs = ResolveInputs(capability, action, fieldValues);

        var invocationId = Guid.NewGuid().ToString("N");
        var context = new SupportSystemInvocationContext
        {
            InstanceId = instanceId,
            InvocationId = invocationId,
            WebhookExpected = capability.SupportedCompletionModes.Contains(SupportSystemCompletionMode.Webhook)
        };

        var policy = SupportCallFailurePolicy.From(action.Parameters);
        var invocation = new SupportSystemInvocation
        {
            InvocationId = invocationId,
            SupportSystemKey = supportSystemKey,
            CapabilityKey = capabilityKey,
            CursorId = cursor.CursorId,
            StageKey = cursor.CurrentNodeKey
        };

        if (Dispatch(new PendingCall(client, capabilityKey, inputs, context), policy, cursor.CursorId) is { } receipt)
        {
            return (invocation with { Receipt = receipt }, false);
        }

        return policy.FallbackOutcome is { } outcome
            ? (invocation with { FailureOutcomeKey = outcome, FailurePayload = policy.FallbackOutputs }, false)
            : (null, true);
    }

    /// <summary>
    /// Sends the call, retrying with a doubling delay up to the policy's <c>retries</c> (none by
    /// default). Returns null once every attempt has failed.
    /// </summary>
    private SupportSystemInvocationReceipt? Dispatch(PendingCall call, SupportCallFailurePolicy policy, string cursorId)
    {
        var delay = policy.InitialRetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return call.Client.InvokeAsync(call.CapabilityKey, call.Inputs, call.Context).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                InvocationFailed(logger, ex, call.Client.SupportSystemKey, call.CapabilityKey, cursorId, attempt + 1);
                if (attempt >= policy.Retries)
                {
                    return null;
                }
            }

            Thread.Sleep(delay);
            delay *= 2;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "support-system-call action on stage '{Stage}' is missing supportSystemKey/capabilityKey; skipped.")]
    private static partial void MissingKeys(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "support-system-call action on stage '{Stage}' references unregistered support system '{System}'/capability '{Capability}'; skipped.")]
    private static partial void UnregisteredCapability(ILogger logger, string stage, string system, string capability);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No ISupportSystemClient registered for support system '{System}'; skipped.")]
    private static partial void NoClient(ILogger logger, string system);

    [LoggerMessage(Level = LogLevel.Error, Message = "Support system '{System}' capability '{Capability}' invocation failed for cursor '{Cursor}' (attempt {Attempt}).")]
    private static partial void InvocationFailed(ILogger logger, Exception exception, string system, string capability, string cursor, int attempt);
}

/// <summary>
/// The result of starting a stage's <c>onEnter</c> support-system calls. When <paramref name="Failed"/>
/// the caller must abandon the advance (save nothing) and re-render the stage with
/// <see cref="SupportCallBatch.UnavailableProblem"/>, so the visitor's next Continue is a retry.
/// </summary>
internal sealed record SupportCallBatch(IReadOnlyList<SupportSystemInvocation> Invocations, bool Failed)
{
    public static SupportCallBatch Empty { get; } = new([], Failed: false);

    public static ServiceRequestProblem UnavailableProblem(string stageKey) => new()
    {
        FieldKey = stageKey,
        Message = "We could not send this to the service just now. Try again, and if it keeps happening, contact us.",
        Code = "SUPPORT_SYSTEM_UNAVAILABLE"
    };
}

internal sealed record PendingCall(
    ISupportSystemClient Client,
    string CapabilityKey,
    IReadOnlyDictionary<string, SupportSystemInputValue> Inputs,
    SupportSystemInvocationContext Context);
