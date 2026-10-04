using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Starts a stage's <c>onEnter</c> <c>support-system-call</c> actions through the registered
/// <see cref="ISupportSystemClient"/>s. See docs/guides/support-systems.md. A misconfigured action or a
/// client that throws is skipped (and logged) rather than failing the request.
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
    public List<SupportSystemInvocation> ExecuteOnEnterSupportSystemActions(
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
            if (action.Timing != ActionTiming.OnEnter
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
            MissingKeys(logger, cursor.CurrentNodeKey);
            return null;
        }

        var capability = SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey);
        if (capability is null)
        {
            UnregisteredCapability(logger, cursor.CurrentNodeKey, supportSystemKey, capabilityKey);
            return null;
        }

        if (!clients.TryGetValue(supportSystemKey, out var client))
        {
            NoClient(logger, supportSystemKey);
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
            InvocationFailed(logger, ex, supportSystemKey, capabilityKey, cursor.CursorId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "support-system-call action on stage '{Stage}' is missing supportSystemKey/capabilityKey; skipped.")]
    private static partial void MissingKeys(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "support-system-call action on stage '{Stage}' references unregistered support system '{System}'/capability '{Capability}'; skipped.")]
    private static partial void UnregisteredCapability(ILogger logger, string stage, string system, string capability);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No ISupportSystemClient registered for support system '{System}'; skipped.")]
    private static partial void NoClient(ILogger logger, string system);

    [LoggerMessage(Level = LogLevel.Error, Message = "Support system '{System}' capability '{Capability}' invocation failed for cursor '{Cursor}'.")]
    private static partial void InvocationFailed(ILogger logger, Exception exception, string system, string capability, string cursor);
}
