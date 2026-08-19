using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Persisted service blueprint contract shared by authoring, seed files and runtime loading.
/// </summary>
public record ServiceBlueprint
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

            if (string.IsNullOrWhiteSpace(gateway.GatewayType))
            {
                // Not a runtime break — Advance() treats anything that isn't exactly "Split" as a
                // Join, so routing through a blank-typed gateway still works for the common single
                // in/single out case. But it IS an authoring-clarity gap (a reader can't tell fan
                // out from pass-through at a glance), so warn rather than stay silent.
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "GATEWAY_MISSING_TYPE",
                    $"gateways[{gatewayIndex}].gatewayType",
                    $"Gateway '{gateway.Key}' has no gatewayType. It still routes correctly (anything " +
                    "other than \"Split\" behaves as a Join), but set it explicitly — \"Split\" for a " +
                    "fan-out, \"Join\" for a merge or plain pass-through — so the shape is clear from " +
                    "the definition alone.",
                    ServiceBlueprintDiagnosticSeverity.Warning));
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
            if (string.Equals(gateway.GatewayType, "Join", StringComparison.OrdinalIgnoreCase)
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

    /// <summary>
    /// Validates that every <see cref="StatGroupComponent"/> item and <see cref="ChartComponent"/>
    /// binds to a field or series that actually exists — either a calculated field/series, or (for
    /// stat-group only) an input component's own <c>fieldKey</c> captured earlier in the blueprint.
    /// Catches the easy authoring mistake of adding a display component whose binding was never
    /// wired to the <c>calculations</c> block (or the block itself was never added), which would
    /// otherwise render silently blank with no error anywhere.
    /// Returns one diagnostic per dangling binding; empty list means every binding resolves.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateDataDisplayBindings()
    {
        var calculatedFieldNames = Calculations?.Fields.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var calculatedSeriesNames = Calculations?.Series?.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var capturedInputFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        // inputFieldKeys is the broader "known field" set DataDisplay bindings below are checked
        // against — genuinely captured inputs, plus a support-system/bulk-dataset-ingest action's
        // own outputs. The CALC_FIELD_SHADOWS_INPUT check further down deliberately uses the
        // narrower capturedInputFieldKeys instead: a service-sourced calculations.fields entry
        // legitimately shares a name with an ingest/support-system output (that's the whole point
        // of declaring one — see docs/guides/bulk-data-review.md, a showWhen expression can't
        // otherwise see it), and is never "shadowing" *user input* the way it would be if a real
        // captured field used that name — conflating the two produced a real false positive here,
        // caught by njf-contributions.json's own contributionsErrorCount field.
        var inputFieldKeys = new HashSet<string>(capturedInputFieldKeys, StringComparer.Ordinal);
        inputFieldKeys.UnionWith(GetSupportSystemOutputFieldKeys());
        inputFieldKeys.UnionWith(GetBulkDatasetIngestOutputFieldKeys());
        var stageKeys = Stages.Select(s => s.StageKey).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        foreach (var stage in Stages)
        {
            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                switch (component)
                {
                    case StatGroupComponent statGroup:
                        if (statGroup.Items.Count == 0)
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_NO_ITEMS",
                                $"{path}.items",
                                $"stat-group '{statGroup.Title}' has no items — it will render nothing. " +
                                "Add at least one item bound to a captured input or calculations.fields entry."));
                        }

                        var itemIndex = 0;
                        foreach (var item in statGroup.Items)
                        {
                            if (string.IsNullOrWhiteSpace(item.FieldKey))
                            {
                                // Distinct from DATA_DISPLAY_UNKNOWN_FIELD below: this isn't a typo pointing
                                // at the wrong name, it's not pointing anywhere at all — a real regression
                                // seen in practice (an agent wired the calculation but left the display
                                // component's binding blank), and one the old "only check non-empty keys"
                                // logic silently let through.
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_MISSING_FIELD",
                                    $"{path}.items[{itemIndex}].fieldKey",
                                    $"stat-group item '{item.Label}' has no fieldKey — it can never bind to " +
                                    "anything and will always render its empty-value placeholder. Set it to a " +
                                    "captured input's fieldKey or a calculations.fields entry."));
                            }
                            else if (!calculatedFieldNames.Contains(item.FieldKey) &&
                                !inputFieldKeys.Contains(item.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_FIELD",
                                    $"{path}.items[{itemIndex}].fieldKey",
                                    $"stat-group item '{item.Label}' binds to field '{item.FieldKey}', which is " +
                                    "neither a captured input field nor a calculations.fields entry. Either add " +
                                    $"'{item.FieldKey}' to the blueprint's calculations block, or fix the fieldKey."));
                            }

                            itemIndex++;
                        }

                        break;

                    case SummaryListComponent summaryList:
                        if (summaryList.Children.Count == 0)
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_NO_ITEMS",
                                $"{path}.children",
                                $"summary-list '{summaryList.Title}' has no children — it will render nothing. " +
                                "Add at least one child bound to a captured input or calculations.fields entry."));
                        }

                        if (!string.IsNullOrWhiteSpace(summaryList.ChangeStateKey) &&
                            !stageKeys.Contains(summaryList.ChangeStateKey))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_CHANGE_STATE",
                                $"{path}.changeStateKey",
                                $"summary-list '{summaryList.Title}' changeStateKey '{summaryList.ChangeStateKey}' " +
                                "is not a stage in this blueprint — its 'Change' link would navigate nowhere. Fix " +
                                "the stage key, or remove changeStateKey if there's nothing to change."));
                        }

                        var childIndex = 0;
                        foreach (var child in summaryList.Children.OfType<InputComponent>())
                        {
                            if (string.IsNullOrWhiteSpace(child.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_MISSING_FIELD",
                                    $"{path}.children[{childIndex}].fieldKey",
                                    $"summary-list child '{child.Label}' has no fieldKey — it can never bind to " +
                                    "anything and will always render its empty-value placeholder. Set it to a " +
                                    "captured input's fieldKey or a calculations.fields entry."));
                            }
                            else if (!calculatedFieldNames.Contains(child.FieldKey) &&
                                !inputFieldKeys.Contains(child.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_FIELD",
                                    $"{path}.children[{childIndex}].fieldKey",
                                    $"summary-list child '{child.Label}' binds to field '{child.FieldKey}', which " +
                                    "is neither a captured input field nor a calculations.fields entry. Either " +
                                    $"add '{child.FieldKey}' to the blueprint's calculations block, or fix the " +
                                    "fieldKey."));
                            }

                            // A row's own ChangeStateKey (for summary lists spanning multiple earlier
                            // stages) needs the same dangling-target check as the component-level one.
                            if (!string.IsNullOrWhiteSpace(child.ChangeStateKey) &&
                                !stageKeys.Contains(child.ChangeStateKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_CHANGE_STATE",
                                    $"{path}.children[{childIndex}].changeStateKey",
                                    $"summary-list child '{child.Label}' changeStateKey '{child.ChangeStateKey}' " +
                                    "is not a stage in this blueprint — its 'Change' link would navigate nowhere. " +
                                    "Fix the stage key, or remove changeStateKey to fall back to the summary-list's " +
                                    "own changeStateKey."));
                            }

                            childIndex++;
                        }

                        break;

                    case ChartComponent chart:
                        if (string.IsNullOrWhiteSpace(chart.Series))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_MISSING_FIELD",
                                $"{path}.series",
                                $"chart '{chart.Title}' has no series set — it can never bind to anything and " +
                                "will always render empty. Set it to a calculations.series entry."));
                        }
                        else if (!calculatedSeriesNames.Contains(chart.Series))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_FIELD",
                                $"{path}.series",
                                $"chart '{chart.Title}' binds to series '{chart.Series}', which is not a " +
                                $"calculations.series entry. Either add '{chart.Series}' to the blueprint's " +
                                "calculations block, or fix the series name."));
                        }

                        break;

                    case BulkDataReviewComponent bulkReview:
                        if (string.IsNullOrWhiteSpace(bulkReview.DatasetIdField))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_MISSING_FIELD",
                                $"{path}.datasetIdField",
                                $"bulk-data-review '{bulkReview.Title}' has no datasetIdField set — it can never " +
                                "bind to a dataset. Set it to match a bulk-dataset-ingest action's own datasetIdField."));
                        }
                        else if (!inputFieldKeys.Contains(bulkReview.DatasetIdField))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_FIELD",
                                $"{path}.datasetIdField",
                                $"bulk-data-review '{bulkReview.Title}' binds to datasetIdField " +
                                $"'{bulkReview.DatasetIdField}', which no bulk-dataset-ingest action in this " +
                                "blueprint declares as its own datasetIdField."));
                        }

                        break;
                }
            }
        }

        if (Calculations is not null)
        {
            foreach (var (name, field) in Calculations.Fields)
            {
                if (string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase) &&
                    capturedInputFieldKeys.Contains(name))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "CALC_FIELD_SHADOWS_INPUT",
                        $"calculations.fields.{name}",
                        $"'{name}' is declared source: \"service\" in calculations, but a component in this " +
                        $"blueprint already captures user input under fieldKey '{name}' — that value is " +
                        "automatically in the calculation scope already. `source: \"service\"` is for a value " +
                        "an external system supplies (e.g. a lookup a host resolves), never for the user's own " +
                        "submitted input. Remove this calculations entry, or use a different field name."));
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Every blueprint field key a registered support system's capability declares in its own
    /// <see cref="SupportSystems.SupportSystemCapabilityDescriptor.Outputs"/>, for every
    /// <c>support-system-call</c> action anywhere in this blueprint that references it —
    /// <see cref="ValidateDataDisplayBindings"/>'s "known field" set for stat-group/summary-list
    /// bindings. An action referencing an unregistered support system or capability contributes
    /// nothing here; that's <see cref="ValidateSupportSystemActions"/>'s own diagnostic to raise.
    /// </summary>
    private HashSet<string> GetSupportSystemOutputFieldKeys()
    {
        var outputFieldKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            foreach (var action in stage.Actions ?? [])
            {
                if (!string.Equals(action.Type, SupportSystems.SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal))
                {
                    continue;
                }

                var supportSystemKey = action.Parameters["supportSystemKey"]?.GetValue<string>();
                var capabilityKey = action.Parameters["capabilityKey"]?.GetValue<string>();
                if (supportSystemKey is null || capabilityKey is null)
                {
                    continue;
                }

                var capability = SupportSystems.SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey);
                if (capability is null)
                {
                    continue;
                }

                foreach (var output in capability.Outputs)
                {
                    outputFieldKeys.Add(output.Key);
                }
            }
        }

        return outputFieldKeys;
    }

    /// <summary>
    /// Validates every <c>support-system-call</c> action against the registered
    /// <see cref="SupportSystems.SupportSystemRegistry"/>: that <c>supportSystemKey</c>/
    /// <c>capabilityKey</c> are present and actually registered, that every input the capability
    /// requires is bound in the action's own <c>params.inputs</c> mapping (and that mapping names
    /// only real declared inputs — catches a typo'd capability input key), that a bound input's
    /// blueprint field key actually exists somewhere in this blueprint, and that the carrying
    /// stage's own outgoing route triggers are all outcomes the capability can actually resolve
    /// to — a route whose trigger isn't one of <see cref="SupportSystems.SupportSystemCapabilityDescriptor.Outcomes"/>
    /// can never fire, since <c>ResolveSupportSystemOutcome</c> (<c>Wayfinder.Engine</c>) only
    /// ever delivers a declared outcome key. See docs/guides/support-systems.md.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateSupportSystemActions()
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>();
        var inputFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, SupportSystems.SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal))
                {
                    continue;
                }

                var supportSystemKey = action.Parameters["supportSystemKey"]?.GetValue<string>();
                var capabilityKey = action.Parameters["capabilityKey"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(supportSystemKey) || string.IsNullOrWhiteSpace(capabilityKey))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_MISSING_KEYS",
                        $"{path}.params",
                        "A support-system-call action must set both params.supportSystemKey and " +
                        "params.capabilityKey."));
                    continue;
                }

                var supportSystem = SupportSystems.SupportSystemRegistry.Find(supportSystemKey);
                if (supportSystem is null)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_UNKNOWN_SUPPORT_SYSTEM",
                        $"{path}.params.supportSystemKey",
                        $"'{supportSystemKey}' is not a registered support system — call " +
                        "list_support_systems to see what's available."));
                    continue;
                }

                var capability = supportSystem.Capabilities.FirstOrDefault(c => c.Key == capabilityKey);
                if (capability is null)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_UNKNOWN_CAPABILITY",
                        $"{path}.params.capabilityKey",
                        $"'{capabilityKey}' is not a capability of support system '{supportSystemKey}'."));
                    continue;
                }

                var inputMapping = action.Parameters["inputs"]?.AsObject();
                var mappedInputKeys = inputMapping?.Select(kvp => kvp.Key).ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);
                var declaredInputKeys = capability.Inputs.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

                foreach (var input in capability.Inputs.Where(i => i.Required))
                {
                    if (!mappedInputKeys.Contains(input.Key))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "SUPPORT_SYSTEM_ACTION_MISSING_REQUIRED_INPUT",
                            $"{path}.params.inputs",
                            $"Capability '{capabilityKey}' requires input '{input.Key}', which this action's " +
                            "params.inputs doesn't bind to a field."));
                    }
                }

                foreach (var mappedKey in mappedInputKeys)
                {
                    if (!declaredInputKeys.Contains(mappedKey))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "SUPPORT_SYSTEM_ACTION_UNKNOWN_INPUT",
                            $"{path}.params.inputs.{mappedKey}",
                            $"'{mappedKey}' is not a declared input of capability '{capabilityKey}' — " +
                            "call list_support_systems to see what it accepts."));
                        continue;
                    }

                    var boundFieldKey = inputMapping?[mappedKey]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(boundFieldKey) || !inputFieldKeys.Contains(boundFieldKey))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "SUPPORT_SYSTEM_ACTION_INPUT_UNKNOWN_FIELD",
                            $"{path}.params.inputs.{mappedKey}",
                            $"Input '{mappedKey}' is bound to field '{boundFieldKey}', which is not a captured " +
                            "input field anywhere in this blueprint."));
                    }
                }

                var declaredOutcomeKeys = capability.Outcomes.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var route in stage.Routes ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(route.Trigger) && !declaredOutcomeKeys.Contains(route.Trigger))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "SUPPORT_SYSTEM_ACTION_ROUTE_TRIGGER_UNKNOWN_OUTCOME",
                            $"stages.{stage.StageKey}.routes",
                            $"Route trigger '{route.Trigger}' on stage '{stage.StageKey}' is not one of capability " +
                            $"'{capabilityKey}''s declared outcomes ({string.Join(", ", declaredOutcomeKeys)}) — it " +
                            "can never fire, since resolving this action only ever delivers a declared outcome key."));
                    }
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Every field key a <c>bulk-dataset-ingest</c> action anywhere in this blueprint declares
    /// via its <c>datasetIdField</c>/<c>errorCountField</c>/<c>warningCountField</c>/
    /// <c>acceptedCountField</c>/<c>dirtyCountField</c> params —
    /// <see cref="ValidateDataDisplayBindings"/>'s "known field" set for stat-group/summary-list
    /// bindings, the same role <see cref="GetSupportSystemOutputFieldKeys"/> plays for a support
    /// system's own declared <c>Outputs</c>.
    /// </summary>
    private HashSet<string> GetBulkDatasetIngestOutputFieldKeys()
    {
        var outputFieldKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            foreach (var action in stage.Actions ?? [])
            {
                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var countFieldParam in new[] { "datasetIdField", "errorCountField", "warningCountField", "acceptedCountField", "dirtyCountField" })
                {
                    var fieldKey = action.Parameters[countFieldParam]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(fieldKey))
                    {
                        outputFieldKeys.Add(fieldKey);
                    }
                }
            }
        }

        return outputFieldKeys;
    }

    /// <summary>
    /// Validates every <c>bulk-dataset-ingest</c>/<c>bulk-dataset-materialize</c> action. An
    /// ingest action must set <c>sourceFileField</c>, resolving to a known field (a captured
    /// input, or a support-system capability's own declared output — the response file from an
    /// external system's <c>support-system-call</c> is the expected common case), and
    /// <c>datasetIdField</c> — the field the minted dataset id is written into, the single
    /// identifier a later <c>bulk-dataset-materialize</c> action or a <c>BulkDataReviewComponent</c>
    /// binds to (deliberately not <c>sourceFileField</c> itself: a materialize action runs on a
    /// different stage than ingest, sometimes several loop rounds later, and only ever has
    /// <c>ServiceRequest.FieldValues</c> to read from — <c>datasetIdField</c> is how it finds
    /// "the dataset ingest already produced" without the engine needing any dataset registry of
    /// its own). It must also declare at least one column, exactly one of which is
    /// <see cref="BulkData.BulkDatasetColumnRole.RowKey"/> (never zero, never more than one — the
    /// external system can only be expected to echo back a single correlation column); no two
    /// columns may share a <c>key</c>; and every column's <c>role</c>/<c>valueKind</c> must be one
    /// of the closed, known vocabularies. A <c>bulk-dataset-materialize</c> action must set
    /// <c>datasetIdField</c> (matching some ingest action's own) and <c>targetFileField</c>. See
    /// docs/guides/bulk-data-review.md.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateBulkDatasetActions()
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>();
        var knownFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        knownFieldKeys.UnionWith(GetSupportSystemOutputFieldKeys());

        var ingestDatasetIdFields = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: bulk-dataset-ingest actions only — collects every declared datasetIdField
        // first, so pass 2 (below) can validate a materialize action against the *complete* set
        // regardless of which stage/action ends up earlier in iteration order.
        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetIngest, StringComparison.Ordinal))
                {
                    continue;
                }

                var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_DATASET_ID_FIELD",
                        $"{path}.params.datasetIdField",
                        "A bulk-dataset-ingest action must set params.datasetIdField."));
                    continue;
                }

                ingestDatasetIdFields.Add(datasetIdField);

                var sourceFileField = action.Parameters["sourceFileField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(sourceFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_SOURCE_FIELD",
                        $"{path}.params.sourceFileField",
                        "A bulk-dataset-ingest action must set params.sourceFileField."));
                }
                else if (!knownFieldKeys.Contains(sourceFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_INVALID_SOURCE_FIELD",
                        $"{path}.params.sourceFileField",
                        $"sourceFileField '{sourceFileField}' is neither a captured input field nor a " +
                        "support system capability's declared output anywhere in this blueprint."));
                }

                var columns = action.Parameters["columns"]?.AsArray();
                if (columns is null || columns.Count == 0)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_COLUMNS",
                        $"{path}.params.columns",
                        "A bulk-dataset-ingest action must declare at least one column."));
                    continue;
                }

                var seenColumnKeys = new HashSet<string>(StringComparer.Ordinal);
                var rowKeyColumnCount = 0;
                var columnIndex = 0;
                foreach (var columnNode in columns)
                {
                    var columnPath = $"{path}.params.columns[{columnIndex}]";
                    columnIndex++;

                    var column = columnNode?.AsObject();
                    var columnKey = column?["key"]?.GetValue<string>();
                    var columnTitle = column?["title"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(columnKey) || string.IsNullOrWhiteSpace(columnTitle))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_INVALID_COLUMN",
                            columnPath,
                            "Every column must set both key and title."));
                        continue;
                    }

                    if (!seenColumnKeys.Add(columnKey))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_DUPLICATE_COLUMN_KEY",
                            $"{columnPath}.key",
                            $"Column key '{columnKey}' is declared more than once."));
                    }

                    var roleValue = column?["role"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(roleValue) ||
                        !Enum.TryParse<BulkData.BulkDatasetColumnRole>(roleValue, out var role))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_UNKNOWN_ROLE",
                            $"{columnPath}.role",
                            $"Column '{columnKey}' has role '{roleValue}', which is not a recognised " +
                            $"BulkDatasetColumnRole ({string.Join(", ", Enum.GetNames<BulkData.BulkDatasetColumnRole>())})."));
                        continue;
                    }

                    if (role == BulkData.BulkDatasetColumnRole.RowKey)
                    {
                        rowKeyColumnCount++;
                    }

                    var valueKindValue = column?["valueKind"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(valueKindValue) ||
                        !Enum.TryParse<ComponentPropertyValueKind>(valueKindValue, out _))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "BULK_DATASET_ACTION_UNKNOWN_VALUE_KIND",
                            $"{columnPath}.valueKind",
                            $"Column '{columnKey}' has valueKind '{valueKindValue}', which is not a recognised " +
                            "ComponentPropertyValueKind."));
                    }
                }

                if (rowKeyColumnCount == 0)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_ROW_KEY",
                        $"{path}.params.columns",
                        "Exactly one column must declare role RowKey — the column the external system is " +
                        "expected to echo back unchanged, used to correlate a row across resubmission rounds. " +
                        "None of this action's columns declare it."));
                }
                else if (rowKeyColumnCount > 1)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_DUPLICATE_ROW_KEY_ROLE",
                        $"{path}.params.columns",
                        $"{rowKeyColumnCount} columns declare role RowKey — exactly one column must, since " +
                        "it's the single correlation key used to match a row across resubmission rounds."));
                }
            }
        }

        // Pass 2: bulk-dataset-materialize actions, validated against the complete set pass 1 collected.
        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, BulkData.BulkDataActionTypes.BulkDatasetMaterialize, StringComparison.Ordinal))
                {
                    continue;
                }

                var datasetIdField = action.Parameters["datasetIdField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_DATASET_ID_FIELD",
                        $"{path}.params.datasetIdField",
                        "A bulk-dataset-materialize action must set params.datasetIdField."));
                }
                else if (!ingestDatasetIdFields.Contains(datasetIdField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_UNKNOWN_DATASET",
                        $"{path}.params.datasetIdField",
                        $"datasetIdField '{datasetIdField}' doesn't match any bulk-dataset-ingest action's own " +
                        "datasetIdField in this blueprint — there's no dataset for this action to materialize."));
                }

                var targetFileField = action.Parameters["targetFileField"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(targetFileField))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "BULK_DATASET_ACTION_MISSING_TARGET_FIELD",
                        $"{path}.params.targetFileField",
                        "A bulk-dataset-materialize action must set params.targetFileField."));
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Validates every <see cref="InputComponent"/>'s <see cref="InputComponent.ConditionalOn"/>
    /// and <see cref="InputComponent.DefaultFrom"/> against what can actually resolve them at
    /// runtime — the same "dangling binding" class of check <see cref="ValidateDataDisplayBindings"/>
    /// already applies to stat-group/summary-list fields, extended to these two properties.
    /// <see cref="InputComponent.ConditionalOn"/> must be another input field's <c>fieldKey</c>
    /// declared in the SAME stage — <c>Wayfinder/Services/Validation/FieldValueValidator.cs</c>
    /// only ever checks it against that stage's own submitted values, so a value pointing anywhere
    /// else (a typo, or a field on a different stage) leaves the field always hidden with nothing
    /// telling the author why. <see cref="InputComponent.DefaultFrom"/> must be a name declared in
    /// <see cref="Calculations"/>' <c>Fields</c> — anything else silently never resolves a default.
    /// Returns one diagnostic per dangling reference; empty list means every one resolves.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateFieldReferences()
    {
        var calculatedFieldNames = Calculations?.Fields.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        foreach (var stage in Stages)
        {
            var stageFieldKeys = stage.Components
                .GetSubmittableInputs()
                .Select(c => c.FieldKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                if (component is not InputComponent input)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(input.ConditionalOn) && !stageFieldKeys.Contains(input.ConditionalOn))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "COMPONENT_UNKNOWN_CONDITIONAL_FIELD",
                        $"{path}.conditionalOn",
                        $"'{input.Label}' is conditional on field '{input.ConditionalOn}', which isn't another " +
                        $"field's fieldKey declared in stage '{stage.StageKey}'. Visibility is only ever checked " +
                        "against the current stage's own submitted values, so this field would always stay " +
                        "hidden. Fix the fieldKey, or remove conditionalOn."));
                }

                if (!string.IsNullOrWhiteSpace(input.DefaultFrom) && !calculatedFieldNames.Contains(input.DefaultFrom))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "COMPONENT_UNKNOWN_DEFAULT_FROM",
                        $"{path}.defaultFrom",
                        $"'{input.Label}' has defaultFrom '{input.DefaultFrom}', which is not a name declared in " +
                        "this blueprint's calculations.fields — the default would never resolve. Fix the name, " +
                        "add it to calculations, or remove defaultFrom."));
                }
            }

            var validationIndex = 0;
            foreach (var rule in stage.Validations ?? [])
            {
                if (!string.IsNullOrWhiteSpace(rule.Field) && !stageFieldKeys.Contains(rule.Field))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "STAGE_VALIDATION_UNKNOWN_FIELD",
                        $"stages.{stage.StageKey}.validations[{validationIndex}].field",
                        $"Validation '{rule.Code}' has field '{rule.Field}', which isn't another field's " +
                        $"fieldKey declared in stage '{stage.StageKey}'. A failure is only ever rendered against " +
                        "the current stage's own inputs, so this would have nothing to attach to. Fix the " +
                        "fieldKey, or remove field for a stage-level (non-field) problem."));
                }

                validationIndex++;
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The only <see cref="StageDefinition.StageType"/> values any authoring surface
    /// recognises. StageType has no runtime meaning on its own — actual step-shell rendering
    /// is inferred from the stage's components (see <c>ComponentExtensions.InferStepType</c>)
    /// — but an unrecognised value passes every other check here and only surfaces later as an
    /// editor-only rejection when someone opens the blueprint in the backoffice Definition tab,
    /// after it's already been saved by another authoring surface (MCP, REST). Kept in sync by
    /// hand with the client's <c>service-blueprint-lint.ts</c> <c>ALLOWED_STAGE_KINDS</c>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownStageKinds =
        ["Question", "CheckAnswers", "Confirmation", "TaskList"];

    /// <summary>
    /// Validates that every stage's optional <see cref="StageDefinition.StageType"/>, when present,
    /// is one of <see cref="KnownStageKinds"/>. StageType is optional — omitting it entirely and
    /// relying on component-based shell inference remains valid — this only rejects a value that's
    /// present but not recognised by any authoring surface.
    /// Returns one diagnostic per stage with an unrecognised stageType; empty list means every
    /// declared stageType (if any) is known.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateStageVocabulary()
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        foreach (var stage in Stages)
        {
            if (!string.IsNullOrWhiteSpace(stage.StageType) && !KnownStageKinds.Contains(stage.StageType))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "STAGE_UNKNOWN_TYPE",
                    $"stages.{stage.StageKey}",
                    $"Stage '{stage.StageKey}' has unrecognised stageType '{stage.StageType}'. Known kinds: " +
                    $"{string.Join(", ", KnownStageKinds)}."));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The only <see cref="RequestPolicy"/> values <see cref="Services.ProcessManagerEngine"/>'s
    /// own instance-lookup switch recognises (<c>GetCurrent</c>'s <c>"multiple"</c>/<c>"prompt"</c>
    /// checks). An unrecognised value isn't rejected at runtime — it just falls straight through
    /// to <c>"single"</c>'s own fallthrough branch with no warning anywhere in the
    /// validate/load/execute path, so a typo (<c>"muliple"</c>) silently changes a blueprint's
    /// concurrency behaviour without ever surfacing as an error. This is the same "fails open,
    /// silently" shape already found and fixed once for <c>showWhen</c> — Warning severity, not
    /// Error, since it never crashes anything; it just isn't what the author almost certainly meant.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownRequestPolicies = ["single", "multiple", "prompt"];

    /// <summary>
    /// Validates that <see cref="RequestPolicy"/> is one of <see cref="KnownRequestPolicies"/>
    /// (case-insensitive). Returns at most one diagnostic; empty list means the declared policy is
    /// recognised.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateRequestPolicy()
    {
        if (KnownRequestPolicies.Any(known => string.Equals(known, RequestPolicy, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return
        [
            new ServiceBlueprintDiagnostic(
                "REQUEST_POLICY_UNKNOWN_VALUE",
                "requestPolicy",
                $"requestPolicy '{RequestPolicy}' isn't one of {string.Join(", ", KnownRequestPolicies)} — " +
                "it silently behaves exactly like 'single' at runtime, with no warning anywhere else. Fix the " +
                "value, or change it deliberately if 'single' really is what's intended.",
                ServiceBlueprintDiagnosticSeverity.Warning)
        ];
    }

    private IReadOnlyList<QueueDefinition>? _queues;

    public string DefinitionKey { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public int Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AuthoredServiceBlueprintId { get; init; }

    public string InitialStage { get; init; } = "";

    public string RequestPolicy { get; init; } = "single";

    public IReadOnlyList<StageDefinition> Stages { get; init; } = Array.Empty<StageDefinition>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QueueDefinition>? Queues
    {
        get => _queues;
        init => _queues = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintGatewayDefinition>? Gateways { get; init; }

    /// <summary>
    /// Declarative calculations for this blueprint: tables, computed fields and series
    /// evaluated by <c>CalculationEvaluator</c> against instance field values plus
    /// host-supplied service inputs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Calculations.ServiceBlueprintCalculationSet? Calculations { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HandoffDefinition>? Handoffs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>
    /// Editor-owned canvas layout hints: manually arranged node positions
    /// keyed by prefixed node id (<c>stage:&lt;stageKey&gt;</c> /
    /// <c>gateway:&lt;key&gt;</c> — an opaque key from the runtime's perspective; nothing here
    /// parses or branches on the prefix). The runtime never reads this — it exists
    /// so authored arrangements survive the save/load round-trip.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceBlueprintLayoutDefinition? Layout { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceBlueprintMetadata? Metadata { get; init; }

}

public record StageDefinition
{
    private string? _queueKey;
    private IReadOnlyList<ServiceBlueprintRouteDefinition>? _routes;

    public string StageKey { get; init; } = "";

    public string DisplayName { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StageType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    public string QueueKey
    {
        get => _queueKey ?? string.Empty;
        init => _queueKey = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }

    public IReadOnlyList<Component> Components { get; init; } = Array.Empty<Component>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintRouteDefinition>? Routes
    {
        get => _routes;
        init => _routes = value;
    }

    /// <summary>
    /// Declarative cross-field business rules checked before this stage is allowed to advance —
    /// see <see cref="ServiceBlueprintStageValidationRule"/>. Evaluated by
    /// <c>ProcessManagerEngine.Advance</c> after field-level validation passes, in addition to
    /// (and ahead of) the <c>ValidateAdvance</c> host hook.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintStageValidationRule>? Validations { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StageMetadata? Metadata { get; init; }

    /// <summary>Curated icon-set key (see the client's graph/node-icons.ts). Falls back to a stage-kind default when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

}

public record RouteFile
{
    public string FromState { get; init; } = "";

    public string ToState { get; init; } = "";

    public string Action { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiresRole { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShowWhen { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }
}

public record QueueDefinition
{
    private string? _key;

    public string Key
    {
        get => _key ?? string.Empty;
        init => _key = value;
    }

    public string DisplayName { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    [JsonIgnore]
    public string? QueueName
    {
        get => string.IsNullOrWhiteSpace(_key) ? null : _key;
        init => _key = value;
    }

    /// <summary>
    /// Which team/skill capabilities may pick up from or start work in this queue — an any-of list
    /// checked against <c>ActorProfile.Capabilities</c> (see <c>ProcessManagerEngine.HasQueueEligibility</c>).
    /// Null/empty (the default) means unrestricted, exactly matching every blueprint that predates
    /// this — the same convention <c>ActorProfile</c>'s own allow-lists already use. Distinct from
    /// <c>IQueueCapabilitiesProvider</c>'s unrelated, pre-existing use of the word "capability"
    /// (which component types a host can render for a queue) — see docs/guides/work-allocation.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    /// <summary>
    /// Null (the default) means legacy: no mandatory-assignment enforcement for this queue —
    /// <c>RequestCursor.AssignedTo</c> governs any optional pickup exactly as it did before this
    /// field existed. <c>"assign-to-initiator"</c>: whoever's action lands work here becomes its
    /// individual owner immediately. <c>"team-tray"</c>: work lands owned by <see cref="OwningTeamId"/>
    /// as a whole, pickable by any member, actionable only once picked up. Orthogonal to
    /// <see cref="RoleGates"/> — RoleGates governs eligibility to see/act on this queue at all;
    /// this governs who, among those already eligible, actually owns a given row. See
    /// docs/guides/team-assignment.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssignmentPolicy { get; init; }

    /// <summary>The team that owns this queue — only meaningful when <see cref="AssignmentPolicy"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwningTeamId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

public record ServiceBlueprintGatewayDefinition
{
    private string? _queueKey;

    public string Key { get; init; } = "";

    public string DisplayName { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    public string GatewayType { get; init; } = "";

    public string QueueKey
    {
        get => _queueKey ?? string.Empty;
        init => _queueKey = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintRouteDefinition>? Routes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitingContent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WaitingExpectedSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WaitingPollIntervalMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WaitingAllowDefer { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitingDeferMessage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredIncomingQueues { get; init; }

    [JsonIgnore]
    public string? QueueName
    {
        get => string.IsNullOrWhiteSpace(_queueKey) ? null : _queueKey;
        init => _queueKey = value;
    }

    [JsonIgnore]
    public string? Source { get; init; }

    [JsonPropertyName("queueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyQueueName
    {
        init
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _queueKey = value;
            }
        }
    }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySource
    {
        init => Source = value;
    }

    /// <summary>Curated icon-set key (see the client's graph/node-icons.ts). Falls back to a Split/Join default when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }
}

public record ServiceBlueprintRouteDefinition
{
    public string Id { get; init; } = "";

    public string Target { get; init; } = "";

    public string Trigger { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiresRole { get; init; }

    /// <summary>
    /// Optional visibility expression, in the same calculation language and evaluated with the
    /// same fail-open bias as <see cref="Components.Component.ShowWhen"/> — when it evaluates to
    /// false this route is excluded from the stage's available actions entirely, not merely
    /// disabled or blocked-with-an-error. Only evaluated for a stage's own routes; it has no
    /// effect on a gateway's own routes (a Split gateway always fans out to every route
    /// regardless, and a Join gateway selects by matching the arriving trigger, not by this) —
    /// <c>ServiceBlueprintAuthoringService.Validate</c> flags a <c>ShowWhen</c> set there as a
    /// diagnostic rather than let it silently do nothing.
    ///
    /// Use this, not a <see cref="ServiceBlueprintStageValidationRule"/> scoped via
    /// <see cref="ServiceBlueprintStageValidationRule.Actions"/>, when a stage has genuinely
    /// different exits and exactly one should be *offered* for a given state of the data — e.g.
    /// "send to insurer" vs. "continue" depending on whether a file was attached. Reach for a
    /// scoped validation rule instead when the exit should always stay offered but needs to be
    /// *blocked with an explanation* until some condition holds — the two are different UX, not
    /// interchangeable spellings of the same thing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShowWhen { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }
}

public record ServiceBlueprintMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AuthoredServiceBlueprintId { get; init; }

    public string? Description { get; init; }

    public string? SchemaVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintGatewayDefinition>? Gateways { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HandoffDefinition>? Handoffs { get; init; }
}

public record HandoffDefinition
{
    public string Id { get; init; } = "";

    public string FromState { get; init; } = "";

    public string ToState { get; init; } = "";

    public string Label { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActorChange { get; init; }
}

public record StageMetadata
{
    private string? _queueKey;

    public string? Description { get; init; }

    public string? StageType { get; init; }

    public string? Actor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueueKey
    {
        get => _queueKey;
        init => _queueKey = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }

    [JsonIgnore]
    public string? QueueName
    {
        get => string.IsNullOrWhiteSpace(_queueKey) ? null : _queueKey;
        init => _queueKey = value;
    }

    [JsonPropertyName("queueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyQueueName
    {
        init
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _queueKey = value;
            }
        }
    }
}

/// <summary>
/// Something a stage or route does beyond just moving between them. Historically schema-only —
/// the engine copies an instance through wherever it's attached (<see cref="StageDefinition.Actions"/>,
/// <see cref="ServiceBlueprintRouteDefinition.Actions"/>) without ever executing it. The first
/// <see cref="Type"/> convention the engine actually executes is
/// <see cref="SupportSystems.SupportSystemActionTypes.SupportSystemCall"/> — see
/// docs/guides/support-systems.md.
/// </summary>
public record ActionDefinition
{
    public string Type { get; init; } = "";

    public string Timing { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("parameterSchemaKey")]
    public string? ParameterSchemaKey { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("params")]
    public JsonObject Parameters { get; init; } = [];
}

/// <summary>
/// Editor canvas layout hints. Positions are whole flow pixels; queue
/// membership stays authoritative on the stages/gateways themselves.
/// </summary>
public record ServiceBlueprintLayoutDefinition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, NodePosition>? Nodes { get; init; }

    /// <summary>
    /// Manual bend point per route edge, keyed by the same "fromId->toId"
    /// edge key the canvas uses for its graph edges. Only set once an author
    /// drags a route; absent routes fall back to the auto-computed path.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, NodePosition>? Routes { get; init; }
}

public record NodePosition
{
    public double X { get; init; }

    public double Y { get; init; }
}
