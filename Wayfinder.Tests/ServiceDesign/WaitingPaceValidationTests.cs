using FluentAssertions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// A join that makes someone wait should say when the answer usually comes back, so its waiting page can tell
/// them what to expect. Leaving it unset still works, but warns.
/// </summary>
public class WaitingPaceValidationTests
{
    private static ServiceBlueprint WithJoin(string paceLine) => ServiceBlueprintYaml.Deserialize($"""
        definitionKey: test
        displayName: Test
        initialStage: start
        stages:
          - stageKey: start
            displayName: Start
            queueKey: visitor
        gateways:
          - key: checked
            displayName: Checking
            gatewayType: Join
            queueKey: visitor
            waitingContent: We are checking.
        {paceLine}
            requiredIncomingQueues:
              - visitor
              - automation
        """);

    [Fact]
    public void AJoinThatMakesSomeoneWait_WithoutAPace_Warns()
    {
        WithJoin("").ValidateWaiting().Should().ContainSingle(d =>
            d.Code == "GATEWAY_WAITING_PACE_UNSET"
            && d.Path == "gateways.checked.waitingPace"
            && d.Severity == ServiceBlueprintDiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("WhileTheyWait")]
    [InlineData("Later")]
    public void AJoinWithAPace_DoesNotWarn(string pace)
    {
        WithJoin($"    waitingPace: {pace}").ValidateWaiting().Should().BeEmpty();
    }

    [Fact]
    public void ThePaceAndIcon_SurviveAYamlRoundTrip()
    {
        var yaml = ServiceBlueprintYaml.Serialize(WithJoin("    waitingPace: WhileTheyWait\n    waitingIcon: Butterfly"));
        var gateway = ServiceBlueprintYaml.Deserialize(yaml).Gateways!.Single();

        gateway.WaitingPace.Should().Be(WaitingPace.WhileTheyWait);
        gateway.WaitingIcon.Should().Be(WaitingIcon.Butterfly);
    }
}
