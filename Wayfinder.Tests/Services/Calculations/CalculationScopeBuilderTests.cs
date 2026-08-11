using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Tests.Services.Calculations;

/// <summary>
/// Covers a real bug found while building the reference app's fire/knives → file-upload
/// showWhen branch: a boolean input field's submitted C# <c>bool</c> gets <c>.ToString()</c>'d
/// to "True"/"False" before reaching calc scope. <see cref="Wayfinder.Services.Calculations.CalculationEvaluator.EvaluateExpression"/>'s
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

    /// <summary>
    /// A field with neither a real submission nor a declared default is not the same as an
    /// unknown/nonexistent field — it's genuinely declared in the blueprint, it just has no
    /// value in this evaluation context (design-time validation, most commonly, which never has
    /// a real citizen behind it). String/boolean fields have a genuinely safe "nothing here"
    /// value — an unfilled text box already means "" and an unticked checkbox already means
    /// false everywhere else in this system — so Build() now uses it rather than leaving the
    /// name unresolvable. Numeric fields deliberately do NOT get this treatment — see the
    /// adjacent NumericFieldWithNoDefaultOrSubmission_StaysAbsentFromScope test.
    /// </summary>
    [Fact]
    public void StringFieldWithNoDefaultOrSubmission_ResolvesToEmptyStringInScope()
    {
        var definition = new ServiceBlueprint
        {
            DefinitionKey = "absent-string-test",
            DisplayName = "Absent string test",
            InitialStage = "only",
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "only",
                    DisplayName = "only",
                    Components = [new TextInputComponent { FieldKey = "applicantName", Label = "Full name" }],
                },
            ],
        };

        var scope = CalculationScopeBuilder.Build(definition, new Dictionary<string, object?>());

        Assert.True(scope.ContainsKey("applicantName"));
        Assert.Equal(string.Empty, scope["applicantName"]);

        // A predicate over it evaluates cleanly rather than throwing "Unknown name".
        var evaluator = new CalculationEvaluator();
        Assert.Equal(false, evaluator.EvaluateExpression("matches(applicantName, 'x')", scope));
    }

    [Fact]
    public void BooleanFieldWithNoDefaultOrSubmission_ResolvesToFalseInScope()
    {
        var definition = BuildBlueprintWithBooleanInput();

        var scope = CalculationScopeBuilder.Build(definition, new Dictionary<string, object?>());

        Assert.True(scope.ContainsKey("hasDangerousProps"));
        Assert.IsType<bool>(scope["hasDangerousProps"]);
        Assert.False((bool)scope["hasDangerousProps"]!);
    }

    /// <summary>
    /// Deliberately unchanged: there is no safe placeholder for a missing number the way "" and
    /// false are safe for string/boolean — 0 is a real, meaningful value a service might act on,
    /// so silently substituting it risks a wrong-but-plausible calculated result. A numeric field
    /// still requires a real submission or an explicit default.
    /// </summary>
    [Fact]
    public void NumericFieldWithNoDefaultOrSubmission_StaysAbsentFromScope()
    {
        var definition = new ServiceBlueprint
        {
            DefinitionKey = "absent-number-test",
            DisplayName = "Absent number test",
            InitialStage = "only",
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "only",
                    DisplayName = "only",
                    Components = [new NumberInputComponent { FieldKey = "jugglerCount", Label = "Jugglers" }],
                },
            ],
        };

        var scope = CalculationScopeBuilder.Build(definition, new Dictionary<string, object?>());

        Assert.False(scope.ContainsKey("jugglerCount"));

        var evaluator = new CalculationEvaluator();
        Assert.Throws<CalculationException>(() => evaluator.EvaluateExpression("jugglerCount * 2", scope));
    }
}
