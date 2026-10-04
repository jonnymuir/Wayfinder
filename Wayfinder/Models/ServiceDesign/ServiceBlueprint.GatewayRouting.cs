using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public partial record ServiceBlueprint
{
    /// <summary>
    /// Validates that every stage route targets a gateway, never another stage directly, that
    /// every gateway has a non-empty <c>key</c> (a keyless gateway can never be a valid route
    /// target — the engine resolves targets by key, so it would silently be unreachable), and
    /// that every route's <c>target</c> actually resolves to an existing gateway (for routes
    /// from a stage) or stage/gateway (for routes from a gateway) — a target that matches
    /// nothing is a dangling reference the engine can't route, and would only surface at
    /// runtime as an opaque "access denied" once a real user reached it.
    /// Gateway routes may target either stages or gateways.
    /// Returns one diagnostic per violation; empty list means the blueprint is valid.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateGatewayRouting()
    {
        var stageKeys = Stages
            .Where(s => !string.IsNullOrWhiteSpace(s.StageKey))
            .Select(s => s.StageKey)
            .ToHashSet(StringComparer.Ordinal);
        var gatewayKeys = (Gateways ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var diagnostics = new List<ServiceBlueprintDiagnostic>();
        var gateways = Gateways ?? [];

        var gatewayIndex = 0;
        foreach (var gateway in gateways)
        {
            if (string.IsNullOrWhiteSpace(gateway.Key))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "GATEWAY_MISSING_KEY",
                    $"gateways[{gatewayIndex}].key",
                    $"Gateway '{gateway.DisplayName}' has no key. The engine resolves route targets " +
                    "by key, so a keyless gateway can never be reached — give it a unique key."));
            }

            if (string.IsNullOrWhiteSpace(gateway.QueueKey))
            {
                // Also not a runtime break for the common case — but the editor canvas visually
                // groups stages and gateways into lanes by queue, so a blank queue here renders the
                // gateway in its own separate lane even when every stage it connects shares one
                // queue, reading as "this got put in a different queue" even though nothing at
                // runtime actually treats it that way.
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "GATEWAY_MISSING_QUEUE",
                    $"gateways[{gatewayIndex}].queueKey",
                    $"Gateway '{gateway.Key}' has no queueKey. Set it to match the queue of the " +
                    "stage(s) that route into it — otherwise the canvas renders it in its own lane, " +
                    "visually separate from a blueprint that's actually all in one queue.",
                    ServiceBlueprintDiagnosticSeverity.Warning));
            }

            gatewayIndex++;
        }

        foreach (var stage in Stages)
        {
            var routeIndex = 0;
            foreach (var route in stage.Routes ?? [])
            {
                if (string.IsNullOrWhiteSpace(route.Target))
                {
                    // Warning, not Error: the visual editor's "add a route" affordance deliberately
                    // supports saving with a route not yet pointed anywhere, mid-edit. But an author
                    // (human or agent) finishing a change should see this before considering the
                    // job done — an empty target left in a "final" save is unreachable at runtime.
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_TARGET_EMPTY",
                        $"stages.{stage.StageKey}.routes[{routeIndex}]",
                        $"Stage '{stage.StageKey}' route '{route.Id}' has no target — it doesn't go " +
                        "anywhere yet. Fine mid-edit; if this blueprint is meant to be complete, wire it " +
                        "to a gateway before finishing.",
                        ServiceBlueprintDiagnosticSeverity.Warning));
                }
                else if (stageKeys.Contains(route.Target))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "GATEWAY_ROUTE_TARGETS_STAGE",
                        $"stages.{stage.StageKey}.routes[{routeIndex}]",
                        $"Stage '{stage.StageKey}' route '{route.Id}' targets stage '{route.Target}' directly. " +
                        "Routes from stages must always target a gateway."));
                }
                else if (!gatewayKeys.Contains(route.Target))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_TARGET_NOT_FOUND",
                        $"stages.{stage.StageKey}.routes[{routeIndex}]",
                        $"Stage '{stage.StageKey}' route '{route.Id}' targets '{route.Target}', which is not " +
                        "any gateway's key in this blueprint. Routes from stages must target an existing gateway."));
                }

                if (string.IsNullOrWhiteSpace(route.Trigger))
                {
                    // Warning, not Error: the engine now defaults a blank trigger to "continue" at
                    // render time, so this no longer breaks the blueprint — but a generic
                    // "Continue" button is rarely what an author actually wants on a human-facing
                    // stage, so it's worth flagging rather than passing silently.
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_TRIGGER_EMPTY",
                        $"stages.{stage.StageKey}.routes[{routeIndex}]",
                        $"Stage '{stage.StageKey}' route '{route.Id}' has no trigger — it will render as a " +
                        "generic \"Continue\" button. Give it a specific trigger (e.g. \"continue\", \"submit\") " +
                        "and label if you want more intentional wording.",
                        ServiceBlueprintDiagnosticSeverity.Warning));
                }

                routeIndex++;
            }
        }

        gatewayIndex = 0;
        foreach (var gateway in gateways)
        {
            // A gateway with zero outgoing routes is a dead end: ProcessManagerEngine's own
            // BuildJoinWaitingEnvelope hard-fails at runtime with GATEWAY_NO_OUTGOING the moment an
            // instance actually reaches it. Reproduced live — an agent-authored gateway saved
            // cleanly with an empty routes array (nothing above checks for *zero* routes, only that
            // each existing route's own target is valid), and the very first real submission that
            // reached it broke with that runtime error. Catch it at design time instead.
            if ((gateway.Routes ?? []).Count == 0)
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "GATEWAY_NO_OUTGOING_ROUTES",
                    $"gateways[{gatewayIndex}].routes",
                    $"Gateway '{gateway.Key}' has no outgoing routes — any instance that reaches it " +
                    "will hard-fail at runtime (GATEWAY_NO_OUTGOING). Add at least one route to a " +
                    "stage or another gateway."));
            }

            var routeIndex = 0;
            foreach (var route in gateway.Routes ?? [])
            {
                if (string.IsNullOrWhiteSpace(route.Target))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_TARGET_EMPTY",
                        $"gateways[{gatewayIndex}].routes[{routeIndex}]",
                        $"Gateway '{gateway.Key}' route '{route.Id}' has no target — it doesn't go " +
                        "anywhere yet. Fine mid-edit; if this blueprint is meant to be complete, wire it " +
                        "to a stage or gateway before finishing.",
                        ServiceBlueprintDiagnosticSeverity.Warning));
                }
                else if (!stageKeys.Contains(route.Target) && !gatewayKeys.Contains(route.Target))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_TARGET_NOT_FOUND",
                        $"gateways[{gatewayIndex}].routes[{routeIndex}]",
                        $"Gateway '{gateway.Key}' route '{route.Id}' targets '{route.Target}', which is not " +
                        "any stage or gateway key in this blueprint."));
                }

                routeIndex++;
            }

            // A Join gateway with more than one outgoing route picks which one to release based on
            // matching its trigger against the action that produced the cursor completing the join
            // (ProcessManagerEngine.TryReleaseJoinIfReady) — so unlike a single-route Join (where the
            // trigger is irrelevant, the one route always fires), a blank or repeated trigger here
            // makes that match impossible or ambiguous and every real instance that reaches it will
            // hard-fail at runtime with GATEWAY_AMBIGUOUS_JOIN_ROUTE.
            if (gateway.GatewayType == GatewayKind.Join
                && (gateway.Routes ?? []).Count > 1)
            {
                var seenTriggers = new HashSet<string>(StringComparer.Ordinal);
                var joinRouteIndex = 0;
                foreach (var route in gateway.Routes ?? [])
                {
                    if (string.IsNullOrWhiteSpace(route.Trigger))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "JOIN_ROUTE_TRIGGER_EMPTY",
                            $"gateways[{gatewayIndex}].routes[{joinRouteIndex}]",
                            $"Join gateway '{gateway.Key}' has {gateway.Routes!.Count} outgoing routes but " +
                            $"route '{route.Id}' has no trigger. A multi-route Join needs a distinct trigger " +
                            "on every route to know which one to take when it releases."));
                    }
                    else if (!seenTriggers.Add(route.Trigger))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "JOIN_ROUTE_TRIGGER_DUPLICATE",
                            $"gateways[{gatewayIndex}].routes[{joinRouteIndex}]",
                            $"Join gateway '{gateway.Key}' has more than one outgoing route with trigger " +
                            $"'{route.Trigger}'. A multi-route Join needs a distinct trigger on every route " +
                            "to know which one to take when it releases."));
                    }

                    joinRouteIndex++;
                }
            }

            gatewayIndex++;
        }

        return diagnostics;
    }
}
