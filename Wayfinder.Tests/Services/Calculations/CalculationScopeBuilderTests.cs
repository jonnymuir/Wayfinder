using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Tests.Services.Calculations;

/// <summary>
/// Covers a real bug found while building the reference app's fire/knives → file-upload
/// showWhen branch: a boolean input field's submitted C# <c>bool</c> gets <c>.ToString()</c>'d
/// to "True"/"False" before reaching calc scope (DescribeInputs classifies it "string", not
/// "number" — deliberately, to avoid touching the number/string type vocabulary the client-side
/// live-model contract ships). <see cref="Wayfinder.Services.Calculations.CalculationEvaluator.EvaluateExpression"/>'s
/// boolean coercion requires a real <c>bool</c>, not that string, so referencing a boolean input
/// bare in a <c>showWhen</c> expression previously always threw and silently fell back to
/// "visible" — regardless of the field's actual value.
/// </summary>
public class CalculationScopeBuilderTests
{
    private static ServiceBlueprint BuildBlueprintWithBooleanInput() => new()
    {
        DefinitionKey = "bool-scope-test",
        DisplayName = "Boolean scope test",
        InitialStage = "only",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "only",
                DisplayName = "Only stage",
                Components =
                [
                    new FieldsetComponent
                    {
                        Children =
                        [
                            new BooleanComponent { FieldKey = "hasDangerousProps", Label = "Dangerous props", Required = false },
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Build_ChecklistBooleanTrue_RoundTripsAsRealBoolInScope()
    {
        var definition = BuildBlueprintWithBooleanInput();
        var fieldValues = new Dictionary<string, object?> { ["hasDangerousProps"] = true };

        var scope = CalculationScopeBuilder.Build(definition, fieldValues);

        Assert.IsType<bool>(scope["hasDangerousProps"]);
        Assert.True((bool)scope["hasDangerousProps"]!);
    }

    [Fact]
    public void Build_CheckboxBooleanFalse_RoundTripsAsRealBoolInScope()
    {
        var definition = BuildBlueprintWithBooleanInput();
        var fieldValues = new Dictionary<string, object?> { ["hasDangerousProps"] = false };

        var scope = CalculationScopeBuilder.Build(definition, fieldValues);

        Assert.IsType<bool>(scope["hasDangerousProps"]);
        Assert.False((bool)scope["hasDangerousProps"]!);
    }

    [Fact]
    public void EvaluateExpression_BareBooleanReference_EvaluatesWithoutThrowingRegardlessOfValue()
    {
        var definition = BuildBlueprintWithBooleanInput();
        var evaluator = new CalculationEvaluator();

        var trueScope = CalculationScopeBuilder.Build(definition, new Dictionary<string, object?> { ["hasDangerousProps"] = true });
        Assert.Equal(true, evaluator.EvaluateExpression("hasDangerousProps", trueScope));

        var falseScope = CalculationScopeBuilder.Build(definition, new Dictionary<string, object?> { ["hasDangerousProps"] = false });
        Assert.Equal(false, evaluator.EvaluateExpression("hasDangerousProps", falseScope));
    }
}
