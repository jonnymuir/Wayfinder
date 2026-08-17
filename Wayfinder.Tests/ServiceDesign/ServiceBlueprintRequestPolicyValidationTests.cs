using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// <see cref="ServiceBlueprint.RequestPolicy"/> has no runtime validation anywhere —
/// <c>ProcessManagerEngine.GetCurrent</c> checks it against "multiple"/"prompt" and falls
/// straight through to "single"'s own behaviour for anything else, silently, with no warning
/// anywhere in the validate/load/execute path. See <see cref="ServiceBlueprint.ValidateRequestPolicy"/>.
/// </summary>
public class ServiceBlueprintRequestPolicyValidationTests
{
    private static ServiceBlueprint Blueprint(string requestPolicy) => new()
    {
        DefinitionKey = "test",
        DisplayName = "Test",
        InitialStage = "only",
        RequestPolicy = requestPolicy,
        Stages =
        [
            new StageDefinition
            {
                StageKey = "only",
                DisplayName = "Only",
                QueueKey = "citizen",
                Components = [new PanelComponent { Heading = "Done" }],
            },
        ],
    };

    [Theory]
    [InlineData("single")]
    [InlineData("multiple")]
    [InlineData("prompt")]
    [InlineData("Single")]
    [InlineData("MULTIPLE")]
    public void KnownPolicy_AnyCasing_ProducesNoDiagnostic(string requestPolicy)
    {
        Blueprint(requestPolicy).ValidateRequestPolicy().Should().BeEmpty();
    }

    [Fact]
    public void UnrecognisedPolicy_ProducesAWarningDiagnostic()
    {
        var diagnostics = Blueprint("muliple").ValidateRequestPolicy();

        diagnostics.Should().ContainSingle(d =>
            d.Code == "REQUEST_POLICY_UNKNOWN_VALUE" &&
            d.Path == "requestPolicy" &&
            d.Severity == ServiceBlueprintDiagnosticSeverity.Warning &&
            d.Message.Contains("muliple"));
    }

    [Fact]
    public void EveryDefaultKnownPolicy_ReflectsTheEngineAndBlueprintDefaultOfSingle()
    {
        // The default ServiceBlueprint.RequestPolicy ("single", per its own property initializer)
        // must itself always be a known, valid policy — a regression here would mean every
        // blueprint that never sets requestPolicy at all starts failing validation.
        Blueprint(new ServiceBlueprint().RequestPolicy).ValidateRequestPolicy().Should().BeEmpty();
    }
}
