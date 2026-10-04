using System.Text.Json.Nodes;
using Wayfinder.Engine.Models;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Builds the response for an instance as one actor sees it: the stage they can act on rendered with its
/// actions, or the join wait screen, an abort notice or an access error. Before showing a wait at a join it gives a
/// pending support-system call a chance to settle (<paramref name="tryPollSupportSystems"/>);
/// <paramref name="buildRenderData"/> is the host's hook for per-stage display data.
/// </summary>
internal sealed class EnvelopeBuilder(
    InstanceRepository instances,
    WorkItemFinder workItems,
    StageCalculations calculations,
    StageRenderer renderer,
    Func<ServiceRequest, ServiceBlueprint, StageDefinition, JsonObject?> buildRenderData,
    Func<ServiceRequest, ServiceBlueprint, ServiceBlueprintGatewayDefinition, bool> tryPollSupportSystems)
    : IEnvelopeSource
{
    public ServiceRequestResponseEnvelope BuildEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId)
    {
        if (instance.IsAborted)
        {
            return Envelopes.Aborted(instance);
        }

        var accessible = workItems.FindActorWorkItems(instance, definition, accessProfile, userId);
        var visibleItem = accessible is [var firstItem, ..] ? firstItem : null;

        if (visibleItem is null)
        {
            return Envelopes.Error(
                "Access denied to the current queue.",
                "ACCESS_DENIED");
        }

        if (visibleItem.IsJoinGateway)
        {
            var joinGateway = FindGateway(definition, visibleItem.StageKey);
            if (joinGateway is not null)
            {
                // A join gateway is exactly where a caseworker's own cursor sits waiting on an
                // automation-queue cursor that's itself waiting on a support-system call — the
                // same "waiting behind the line of visibility" screen citizen/caseworker joins
                // already use. Before rendering that wait screen again, give any still-pending
                // support-system invocation blocking THIS gateway a chance to resolve via poll —
                // the generic, always-on counterpart to the webhook receiver resolving one
                // asynchronously. If anything resolved, its own Advance() call already saved
                // fresh state (and possibly released the join outright); re-derive the response
                // from that fresh state rather than the now-stale `instance` this method started
                // with.
                if (tryPollSupportSystems(instance, definition, joinGateway)
                    && instances.TryGet(instance.InstanceId, out var refreshed))
                {
                    return BuildEnvelope(refreshed, definition, accessProfile, userId);
                }

                return Envelopes.JoinWaiting(instance, definition, joinGateway);
            }
        }

        var stage = definition.Stages.FirstOrDefault(s => s.StageKey == visibleItem.StageKey);
        if (stage == null)
        {
            return Envelopes.Error(
                $"State '{visibleItem.StageKey}' not found in definition '{definition.DefinitionKey}'.",
                "STATE_NOT_FOUND");
        }

        var renderData = buildRenderData(instance, definition, stage);
        var calc = calculations.EvaluateDefinitionCalculations(instance, definition, stage);
        if (calc is not null)
        {
            renderData ??= new JsonObject();
            renderData["live"] = StageCalculations.BuildLiveModel(definition, calc);
        }

        var components = renderer.BuildComponents(stage.Components, instance.FieldValues, calc);
        var effectiveStepType = stage.Components.InferStepType();
        var waitingComponent = stage.Components.OfType<WaitingComponent>().FirstOrDefault();

        var render = new StepContent
        {
            StepType = effectiveStepType,
            StateDisplayName = stage.DisplayName,
            Components = components,
            AvailableActions = visibleItem.AvailableActions.ToArray(),
            Data = renderData
        };

        var responseState = effectiveStepType switch
        {
            "status-timeline" => "defer",
            "confirmation" => "complete",
            _ => "render"
        };

        return new ServiceRequestResponseEnvelope
        {
            InstanceId = instance.InstanceId,
            ResponseState = responseState,
            StateVersion = instance.StateVersion,
            CorrelationId = instance.InstanceId,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            PollAfterMs = waitingComponent?.PollIntervalMs,
            Render = render,
            RequestPolicy = definition.RequestPolicy,
            AllowManualRestart = definition.AllowManualRestart
        };
    }
}
