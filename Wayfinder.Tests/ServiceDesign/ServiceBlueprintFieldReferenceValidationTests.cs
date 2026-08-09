using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// Extends the same "dangling binding" class of check <see cref="ServiceBlueprint.ValidateDataDisplayBindings"/>
/// already applies to stat-group/summary-list fields to <see cref="InputComponent.ConditionalOn"/>/
/// <see cref="InputComponent.DefaultFrom"/> — see <see cref="ServiceBlueprint.ValidateFieldReferences"/>.
/// This is what a properties-panel dropdown enforces visually in the editor client, and what an
/// MCP-driven authoring agent gets back from <c>validate_service_blueprint</c> when it sets one of
/// these to a value that doesn't exist — an agent has no browser to look at a blank dropdown in.
/// </summary>
public class ServiceBlueprintFieldReferenceValidationTests
{
    private static StageDefinition Stage(string key, params Component[] components) => new()
    {
        StageKey = key,
        DisplayName = key,
        QueueKey = "citizen",
        Components = components,
    };

    [Fact]
    public void ConditionalOn_PointingAtARealSiblingFieldInTheSameStage_ProducesNoDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Stages =
            [
                Stage("only",
                    new BooleanComponent { FieldKey = "hasPet", Label = "Do you have a pet?" },
                    new TextInputComponent { FieldKey = "petName", Label = "Pet's name", ConditionalOn = "hasPet", VisibleWhen = "true" }),
            ],
        };

        blueprint.ValidateFieldReferences().Should().BeEmpty();
    }

    [Fact]
    public void ConditionalOn_PointingAtANonExistentField_ProducesUnknownConditionalFieldDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Stages =
            [
                Stage("only",
                    new TextInputComponent { FieldKey = "petName", Label = "Pet's name", ConditionalOn = "hasPett", VisibleWhen = "true" }),
            ],
        };

        var diagnostics = blueprint.ValidateFieldReferences();

        diagnostics.Should().ContainSingle(d =>
            d.Code == "COMPONENT_UNKNOWN_CONDITIONAL_FIELD" &&
            d.Path == "stages.only.components[0].conditionalOn" &&
            d.Message.Contains("hasPett"));
    }

    [Fact]
    public void ConditionalOn_PointingAtAFieldOnADifferentStage_IsStillFlagged()
    {
        // FieldValueValidator only ever checks conditionalOn against the CURRENT stage's own
        // submitted values — a field on another stage can never satisfy it, even though it's a
        // real field key somewhere in the blueprint.
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "first",
            Stages =
            [
                Stage("first", new BooleanComponent { FieldKey = "hasPet", Label = "Do you have a pet?" }),
                Stage("second", new TextInputComponent { FieldKey = "petName", Label = "Pet's name", ConditionalOn = "hasPet", VisibleWhen = "true" }),
            ],
        };

        blueprint.ValidateFieldReferences().Should().ContainSingle(d => d.Code == "COMPONENT_UNKNOWN_CONDITIONAL_FIELD");
    }

    [Fact]
    public void ConditionalOn_NestedInsideAFieldset_StillResolvesAgainstItsWholeStage()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Stages =
            [
                Stage("only",
                    new BooleanComponent { FieldKey = "hasPet", Label = "Do you have a pet?" },
                    new FieldsetComponent
                    {
                        Legend = "Pet details",
                        Children = [new TextInputComponent { FieldKey = "petName", Label = "Pet's name", ConditionalOn = "hasPet", VisibleWhen = "true" }],
                    }),
            ],
        };

        blueprint.ValidateFieldReferences().Should().BeEmpty();
    }

    [Fact]
    public void DefaultFrom_PointingAtARealCalculationField_ProducesNoDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField> { ["suggestedName"] = new() { Expr = "1" } },
            },
            Stages = [Stage("only", new TextInputComponent { FieldKey = "petName", Label = "Pet's name", DefaultFrom = "suggestedName" })],
        };

        blueprint.ValidateFieldReferences().Should().BeEmpty();
    }

    [Fact]
    public void DefaultFrom_PointingAtANonExistentCalculationField_ProducesUnknownDefaultFromDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField> { ["suggestedName"] = new() { Expr = "1" } },
            },
            Stages = [Stage("only", new TextInputComponent { FieldKey = "petName", Label = "Pet's name", DefaultFrom = "suggestdName" })],
        };

        blueprint.ValidateFieldReferences().Should().ContainSingle(d =>
            d.Code == "COMPONENT_UNKNOWN_DEFAULT_FROM" &&
            d.Path == "stages.only.components[0].defaultFrom" &&
            d.Message.Contains("suggestdName"));
    }

    [Fact]
    public void DefaultFrom_WithNoCalculationsBlockAtAll_ProducesUnknownDefaultFromDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Stages = [Stage("only", new TextInputComponent { FieldKey = "petName", Label = "Pet's name", DefaultFrom = "anything" })],
        };

        blueprint.ValidateFieldReferences().Should().ContainSingle(d => d.Code == "COMPONENT_UNKNOWN_DEFAULT_FROM");
    }

    [Fact]
    public void EmptyConditionalOnAndDefaultFrom_ProduceNoDiagnostics()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "only",
            Stages = [Stage("only", new TextInputComponent { FieldKey = "petName", Label = "Pet's name" })],
        };

        blueprint.ValidateFieldReferences().Should().BeEmpty();
    }
}
