using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>The fixed-shape responses that are not a rendered stage: an error, an aborted instance, a join wait screen.</summary>
internal static class Envelopes
{
    public static ServiceRequestResponseEnvelope Error(string message, string code) =>
        new()
        {
            InstanceId = string.Empty,
            ResponseState = "error",
            StateVersion = 0,
            CorrelationId = Guid.NewGuid().ToString(),
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Problems = [new ServiceRequestProblem { FieldKey = string.Empty, Message = message, Code = code }]
        };

    /// <summary>
    /// The uniform response for any render or advance attempt against an instance an admin has
    /// stopped (<see cref="ServiceRequest.IsAborted"/>) — reuses the existing "error" response
    /// shape every host already renders, rather than a new <c>ResponseState</c> value a host would
    /// need new handling for. Carries the real <c>InstanceId</c> (unlike <see cref="ErrorEnvelope"/>,
    /// which never does) so a host can still log/link back to exactly which instance this was.
    /// </summary>
    public static ServiceRequestResponseEnvelope Aborted(ServiceRequest instance) =>
        new()
        {
            InstanceId = instance.InstanceId,
            ResponseState = "error",
            StateVersion = instance.StateVersion,
            CorrelationId = instance.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Problems =
            [
                new ServiceRequestProblem
                {
                    FieldKey = string.Empty,
                    Message = "This service request was stopped by an administrator" +
                        (string.IsNullOrWhiteSpace(instance.AbortedReason) ? "." : $": {instance.AbortedReason}"),
                    Code = "INSTANCE_ABORTED"
                }
            ]
        };

    /// <summary>
    /// A move that was saved and took the request somewhere the actor who made it can no longer see: their
    /// part is done (a team's reply handed back to the person who wrote in, say). Nothing to render and
    /// nothing wrong, so no problems; the instance and its new version say what was saved.
    /// </summary>
    public static ServiceRequestResponseEnvelope MovedOn(ServiceRequest saved)
    {
        return new()
        {
            InstanceId = saved.InstanceId,
            ResponseState = "moved_on",
            StateVersion = saved.StateVersion,
            CorrelationId = saved.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow
        };
    }

    public static ServiceRequestResponseEnvelope JoinWaiting(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ServiceBlueprintGatewayDefinition joinGateway)
    {
        var waitingContent = joinGateway.WaitingContent
                             ?? "Please wait while other parts of this blueprint are completed.";
        var pollMs = joinGateway.WaitingPollIntervalMs > 0 ? joinGateway.WaitingPollIntervalMs : 3000;
        var expectedSeconds = joinGateway.WaitingExpectedSeconds > 0 ? joinGateway.WaitingExpectedSeconds : 30;
        var allowDefer = joinGateway.WaitingDeferMessage is not null || joinGateway.WaitingAllowDefer;

        var waitingFor = PendingQueueNames(instance, definition, joinGateway);

        var render = new StepContent
        {
            StepType = "status-timeline",
            StateDisplayName = joinGateway.DisplayName,
            Components =
            [
                new ComponentRenderPayload
                {
                    Type = "waiting",
                    Content = waitingContent,
                    WaitingFor = waitingFor,
                    ExpectedWaitSeconds = expectedSeconds,
                    PollIntervalMs = pollMs,
                    AllowDefer = allowDefer,
                    DeferMessage = joinGateway.WaitingDeferMessage
                }
            ],
            AvailableActions = Array.Empty<ServiceRequestAction>()
        };

        return new ServiceRequestResponseEnvelope
        {
            InstanceId = instance.InstanceId,
            ResponseState = "defer",
            StateVersion = instance.StateVersion,
            CorrelationId = instance.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            PollAfterMs = pollMs,
            Render = render,
            RequestPolicy = definition.RequestPolicy,
            AllowManualRestart = definition.AllowManualRestart
        };
    }

    /// <summary>The display names of the queues this join is still waiting on (never their internal keys, which a
    /// visitor must not see).</summary>
    private static string[] PendingQueueNames(
        ServiceRequest instance, ServiceBlueprint definition, ServiceBlueprintGatewayDefinition joinGateway) =>
        (joinGateway.RequiredIncomingQueues ?? [])
            .Where(queue => !instance.Cursors.Any(c =>
                c.IsAtGateway
                && string.Equals(c.CurrentNodeKey, joinGateway.Key, StringComparison.Ordinal)
                && string.Equals(c.QueueKey, queue, StringComparison.Ordinal)))
            .Select(queue => GetQueues(definition).FirstOrDefault(q => string.Equals(q.Key, queue, StringComparison.Ordinal))?.DisplayName is { Length: > 0 } name
                ? name
                : queue)
            .ToArray();

    // ─── Support system helpers ──────────────────────────────────────────────
}
