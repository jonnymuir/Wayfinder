using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Serialization;

namespace Wayfinder.Tests.ServiceDesign.Serialization;

public class ServiceBlueprintYamlTests
{
    private const string Minimal = """
        definitionKey: apply
        displayName: Apply
        initialStage: start
        stages:
          - stageKey: start
            displayName: Start
            components:
              - type: text
                fieldKey: name
                label: Your name
        """;

    [Fact]
    public void ABlueprint_ReadsFromYaml_WithEachComponentAsItsRegisteredType()
    {
        var blueprint = ServiceBlueprintYaml.Deserialize(Minimal);

        blueprint.DefinitionKey.Should().Be("apply");
        blueprint.Stages.Single().Components.Single().Should().BeOfType<TextInputComponent>()
            .Which.Label.Should().Be("Your name");
    }

    [Fact]
    public void WritingThenReading_GivesBackTheSameBlueprint()
    {
        var blueprint = ServiceBlueprintYaml.Deserialize(Minimal);

        var again = ServiceBlueprintYaml.Deserialize(ServiceBlueprintYaml.Serialize(blueprint));

        ServiceBlueprintYaml.Serialize(again).Should().Be(ServiceBlueprintYaml.Serialize(blueprint));
        again.Stages.Single().Components.Single().Should().BeOfType<TextInputComponent>();
    }

    [Fact]
    public void AComponent_IsWrittenWithItsTypeFirst_AndNullsLeftOut()
    {
        var yaml = ServiceBlueprintYaml.Serialize(ServiceBlueprintYaml.Deserialize(Minimal));

        yaml.Should().Contain("      - type: text\n        fieldKey: name\n        label: Your name\n");
        yaml.Should().NotContain("hint");
    }

    [Fact]
    public void MultiLineText_IsWrittenAsALiteralBlock()
    {
        var yaml = ServiceBlueprintYaml.Serialize(new BodyComponent { Content = "First paragraph.\n\nSecond paragraph." });

        yaml.Should().Contain("content: |-\n  First paragraph.\n\n  Second paragraph.");
    }

    [Fact]
    public void AnUnknownKey_IsAnErrorAtItsLine_NotSilentlyDropped()
    {
        var act = () => ServiceBlueprintYaml.Deserialize(Minimal.Replace("label: Your name", "lable: Your name", StringComparison.Ordinal));

        act.Should().Throw<ServiceBlueprintYamlException>()
            .Where(ex => ex.Line == 10 && ex.Reason == "Property 'lable' not found on type 'TextInputComponent'.");
    }

    [Fact]
    public void AnUnknownComponentType_NamesTheTypesThatExist()
    {
        var act = () => ServiceBlueprintYaml.Deserialize(Minimal.Replace("type: text", "type: txet", StringComparison.Ordinal));

        act.Should().Throw<ServiceBlueprintYamlException>()
            .Where(ex => ex.Message.Contains("'txet' is not a component type", StringComparison.Ordinal)
                && ex.Message.Contains("summary-list", StringComparison.Ordinal));
    }

    [Fact]
    public void AComponentWithNoType_SaysItNeedsOne()
    {
        var act = () => ServiceBlueprintYaml.Deserialize<Component>("label: Orphan");

        act.Should().Throw<ServiceBlueprintYamlException>().WithMessage("*needs a 'type'*");
    }

    // Security regression: anchors and aliases let a few lines expand exponentially when the
    // blueprint is next written out (a "billion laughs"). Blueprints arrive from agents and
    // remote callers, so they are refused at the reader.
    [Theory]
    [InlineData("definitionKey: &key apply\ndisplayName: *key\n")]
    [InlineData("definitionKey: apply\ntags: &t { a: b }\nlayout: *t\n")]
    public void AnchorsAndAliases_AreRefused(string yaml)
    {
        var act = () => ServiceBlueprintYaml.Deserialize(yaml);

        act.Should().Throw<ServiceBlueprintYamlException>().WithMessage("*Anchors and aliases*");
    }

    [Fact]
    public void ActionParams_ArePlainValues_TypedAsYamlWritesThem()
    {
        var action = ServiceBlueprintYaml.Deserialize<ActionDefinition>("""
            type: support-system-call
            timing: onEnter
            params:
              retries: 2
              ratio: 0.5
              enabled: true
              code: "007"
              nothing: ~
              inputs:
                file: riskAssessment
              columns:
                - one
                - 2
            """);

        action.Timing.Should().Be(ActionTiming.OnEnter);
        action.Parameters.GetInt("retries").Should().Be(2);
        action.Parameters["ratio"].Should().Be(0.5m);
        action.Parameters.GetBool("enabled").Should().BeTrue();
        action.Parameters.GetString("code").Should().Be("007");
        action.Parameters["nothing"].Should().BeNull();
        action.Parameters.GetMap("inputs")!.GetString("file").Should().Be("riskAssessment");
        action.Parameters.GetList("columns").Should().Equal("one", 2L);
    }

    [Fact]
    public void AQuotedNumberInParams_StaysQuotedWhenWritten()
    {
        var action = new ActionDefinition
        {
            Type = "x",
            Timing = ActionTiming.OnEnter,
            Parameters = new Dictionary<string, object?> { ["code"] = "007" },
        };

        ServiceBlueprintYaml.Deserialize<ActionDefinition>(ServiceBlueprintYaml.Serialize(action))
            .Parameters.GetString("code").Should().Be("007");
    }

    [Fact]
    public void TryDeserialize_TurnsUnreadableTextIntoAnInvalidYamlDiagnostic()
    {
        var ok = ServiceBlueprintYaml.TryDeserialize("stages: [unclosed", out var blueprint, out var diagnostic);

        ok.Should().BeFalse();
        blueprint.Should().BeNull();
        diagnostic!.Code.Should().Be("INVALID_YAML");
        diagnostic.Message.Should().Contain("Line 2, column 1");
    }

    [Fact]
    public void JsonText_IsAlsoReadable_BecauseYamlIsASupersetOfJson()
    {
        var blueprint = ServiceBlueprintYaml.Deserialize("""{ "definitionKey": "apply", "stages": [] }""");

        blueprint.DefinitionKey.Should().Be("apply");
    }
}
