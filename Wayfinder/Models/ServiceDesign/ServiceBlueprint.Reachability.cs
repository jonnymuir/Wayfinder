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
    /// Validates that every stage and gateway can eventually reach a terminal stage (one with no
    /// outgoing routes) via *some* path — not that every path does, so a deliberate self-loop (e.g.
    /// money-modeller's <c>recalculate</c> route back to <c>model</c>) is fine as long as another
    /// route out of the same stage still leads somewhere. Reproduced live: an agent-authored
    /// "request more info" gateway that only ever routed within the requesting queue, with no path
    /// back to a stage where the other queue's actor could actually supply what was requested —
    /// <see cref="ValidateGatewayRouting"/> passed (every gateway had outgoing routes, every target
    /// resolved) but any real instance that took that branch could never complete. This check
    /// doesn't understand *why* a path is a dead end — that's a service-design judgement call it
    /// can't make — only that one exists structurally.
    /// Returns one diagnostic per stage or gateway that can never reach a terminal stage; empty
    /// list means every node can eventually complete.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateReachability()
    {
        var gateways = Gateways ?? [];
        var stageKeys = Stages
            .Where(s => !string.IsNullOrWhiteSpace(s.StageKey))
            .Select(s => s.StageKey)
            .ToHashSet(StringComparer.Ordinal);
        var gatewayKeys = gateways
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        // Predecessor map built only from routes that resolve to a real node — a dangling target
        // is already reported by ValidateGatewayRouting, so it's silently skipped here rather than
        // double-reported as an unreachable dead end too.
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void AddEdge(string from, string to)
        {
            if (!predecessors.TryGetValue(to, out var list))
            {
                list = [];
                predecessors[to] = list;
            }

            list.Add(from);
        }

        foreach (var stage in Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.StageKey))
            {
                continue;
            }

            foreach (var route in stage.Routes ?? [])
            {
                if (gatewayKeys.Contains(route.Target))
                {
                    AddEdge(stage.StageKey, route.Target);
                }
            }
        }

        foreach (var gateway in gateways)
        {
            if (string.IsNullOrWhiteSpace(gateway.Key))
            {
                continue;
            }

            foreach (var route in gateway.Routes ?? [])
            {
                if (stageKeys.Contains(route.Target) || gatewayKeys.Contains(route.Target))
                {
                    AddEdge(gateway.Key, route.Target);
                }
            }
        }

        var terminalStates = Stages
            .Where(s => !string.IsNullOrWhiteSpace(s.StageKey) && (s.Routes ?? []).Count == 0)
            .Select(s => s.StageKey)
            .ToList();

        var reachable = new HashSet<string>(terminalStates, StringComparer.Ordinal);
        var queue = new Queue<string>(terminalStates);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!predecessors.TryGetValue(current, out var preds))
            {
                continue;
            }

            foreach (var predecessor in preds)
            {
                if (reachable.Add(predecessor))
                {
                    queue.Enqueue(predecessor);
                }
            }
        }

        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        var gatewayIndex = 0;
        foreach (var gateway in gateways)
        {
            if (!string.IsNullOrWhiteSpace(gateway.Key) && !reachable.Contains(gateway.Key))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "GATEWAY_UNREACHABLE_TERMINAL",
                    $"gateways[{gatewayIndex}]",
                    $"Gateway '{gateway.Key}' can never reach a completed stage — every path leaving " +
                    "it eventually loops back without an exit. If this is a deliberate wait/retry " +
                    "loop, add a route somewhere in the loop that leads onward to a stage with no " +
                    "outgoing routes."));
            }

            gatewayIndex++;
        }

        foreach (var stage in Stages)
        {
            if (!string.IsNullOrWhiteSpace(stage.StageKey) &&
                (stage.Routes ?? []).Count > 0 &&
                !reachable.Contains(stage.StageKey))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "STAGE_UNREACHABLE_TERMINAL",
                    $"stages.{stage.StageKey}",
                    $"Stage '{stage.StageKey}' can never reach a completed stage — every route out of " +
                    "it eventually loops back without an exit."));
            }
        }

        return diagnostics;
    }
}
