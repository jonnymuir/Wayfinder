using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

/// <summary>
/// Proves <see cref="ServiceBlueprintAuthoringService.Validate"/> statically checks every
/// <see cref="ServiceBlueprintRouteDefinition.ShowWhen"/> expression before a blueprint can be
/// saved — the same treatment already applied to <see cref="Component.ShowWhen"/> and stage
/// validation <c>when</c>/<c>rule</c> — plus the gateway-route footgun this replaces the old
/// always/event/guard route-condition UI for: <c>ShowWhen</c> genuinely does nothing on a
/// gateway's own routes (see ProcessManagerEngineRouteShowWhenTests for the runtime half).
/// </summary>
public class ServiceBlueprintAuthoringServiceRouteShowWhenTests
{
    private sealed class UnusedStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static readonly ServiceBlueprintAuthoringService Service = new(new UnusedStore());

    private static ServiceBlueprint BlueprintWithStageRoute(string? showWhen) => new()
    {
        DefinitionKey = "route-show-when-diagnostics-test",
        DisplayName = "Test",
        InitialStage = "review",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "review",
                DisplayName = "Review",
                QueueKey = "caseworker",
                Components = [new TextInputComponent { FieldKey = "notes", Label = "Notes", Default = "" }],
                // Every stage route targets a real gateway (ValidateGatewayRouting's own rule) —
                // even this trivial single-route handoff needs its own pass-through gateway.
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "review--continue", Target = "to-done", Trigger = "continue", ShowWhen = showWhen,
                    },
                ],
            },
            new StageDefinition { StageKey = "done", DisplayName = "Done", QueueKey = "caseworker" },
        ],
        Gateways =
        [
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-done",
                DisplayName = "Continue to done",
                GatewayType = "Split",
                QueueKey = "caseworker",
                Routes = [new ServiceBlueprintRouteDefinition { Id = "to-done--continue", Target = "done", Trigger = "continue" }],
            },
        ],
    };

    [Fact]
    public void WellFormedShowWhen_ProducesNoDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithStageRoute(showWhen: "notes = ''"));

        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("ROUTE_SHOW_WHEN_"));
    }

    [Fact]
    public void NoShowWhen_ProducesNoDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithStageRoute(showWhen: null));

        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("ROUTE_SHOW_WHEN_"));
        outcome.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ShowWhenReferencingUnknownName_ProducesEvalErrorDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithStageRoute(showWhen: "nosuchthing"));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "ROUTE_SHOW_WHEN_EVAL_ERROR" &&
            d.Path == "stages.review.routes[0].showWhen" &&
            d.Message.Contains("nosuchthing"));
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ShowWhenThatEvaluatesToANumber_ProducesNoDiagnostic()
    {
        // Unlike a stage validation rule, ShowWhen is a display hint (ProcessManagerEngine's
        // EvaluateShowWhen treats any non-false result as visible, the exact same tolerance
        // Component.ShowWhen already has) — a clean non-boolean result is not an authoring
        // mistake here the way it would be for StageDefinition.Validations' rule.
        var outcome = Service.Validate(BlueprintWithStageRoute(showWhen: "1 + 1"));

        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("ROUTE_SHOW_WHEN_"));
    }

    private static ServiceBlueprint BlueprintWithGatewayRoute(string showWhen) => new()
    {
        DefinitionKey = "route-show-when-gateway-diagnostics-test",
        DisplayName = "Test",
        InitialStage = "review",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "review",
                DisplayName = "Review",
                QueueKey = "caseworker",
                Routes = [new ServiceBlueprintRouteDefinition { Id = "review--continue", Target = "fan-out", Trigger = "continue" }],
            },
            new StageDefinition { StageKey = "done", DisplayName = "Done", QueueKey = "caseworker" },
        ],
        Gateways =
        [
            new ServiceBlueprintGatewayDefinition
            {
                Key = "fan-out",
                DisplayName = "Fan out",
                GatewayType = "Split",
                QueueKey = "caseworker",
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "fan-out--continue", Target = "done", Trigger = "continue", ShowWhen = showWhen,
                    },
                ],
            },
        ],
    };

    [Fact]
    public void ShowWhenOnAGatewayRoute_ProducesInertWarningDiagnostic()
    {
        // BuildAvailableActions never runs for a gateway's own routes — a Split always fans out
        // to every route regardless, a Join selects by matching the arriving trigger — so a
        // ShowWhen set here would silently do nothing, exactly the class of bug replacing the old
        // always/event/guard route-condition UI was meant to close, not reopen one level down.
        var outcome = Service.Validate(BlueprintWithGatewayRoute(showWhen: "true"));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "ROUTE_SHOW_WHEN_ON_GATEWAY_ROUTE" &&
            d.Path == "gateways.fan-out.routes[0].showWhen" &&
            d.Severity == ServiceBlueprintDiagnosticSeverity.Warning);
        // A warning, not a blocker — the blueprint still saves; the author just gets told the
        // expression they wrote has no effect where they put it.
        outcome.IsValid.Should().BeTrue();
    }
}
