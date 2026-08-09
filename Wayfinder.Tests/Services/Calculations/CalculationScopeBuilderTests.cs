using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
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

    /// <summary>
    /// Real bug, found via Umbraco.Prism's own MoneyModellerCalculationTests once a Wayfinder
    /// release finally reached a summary-list that echoes a calculated field's own name (the
    /// standard check-your-answers pattern: a <c>SummaryListComponent</c> child reuses an
    /// <c>InputComponent</c>-derived type purely for its rendering shape, never as a genuine
    /// second input). Before <see cref="ComponentExtensions.GetSubmittableInputs"/> existed,
    /// <see cref="CalculationScopeBuilder.DescribeInputs"/> used <see cref="ComponentExtensions.GetAllInputs"/>,
    /// which reached the summary-list child too — so a resubmitted formatted display value
    /// (exactly what the engine itself writes back to a client) landed in the scope under the
    /// same key as the calculated field, and evaluating that field then threw "Field 'x' collides
    /// with an input or earlier field."
    /// </summary>
    [Fact]
    public void Build_SummaryListChildEchoingACalculatedFieldsOwnName_DoesNotAddItToScope()
    {
        var definition = new ServiceBlueprint
        {
            DefinitionKey = "summary-echo-test",
            DisplayName = "Summary echo test",
            InitialStage = "result",
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField>
                {
                    ["total"] = new() { Expr = "100" },
                },
            },
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "result",
                    DisplayName = "Result",
                    Components =
                    [
                        new SummaryListComponent
                        {
                            Title = "Your result",
                            Children = [new TextInputComponent { FieldKey = "total", Label = "Total" }],
                        },
                    ],
                },
            ],
        };

        // Simulates the engine's own write-back: a formatted display value resubmitted under
        // the same key as the calculated field it echoes.
        var fieldValues = new Dictionary<string, object?> { ["total"] = "£100" };

        var scope = CalculationScopeBuilder.Build(definition, fieldValues);
        Assert.False(scope.ContainsKey("total"));

        var evaluator = new CalculationEvaluator();
        var result = evaluator.Evaluate(definition.Calculations!, scope);
        Assert.Equal(100m, (decimal)result.Fields["total"]!);
    }
}
