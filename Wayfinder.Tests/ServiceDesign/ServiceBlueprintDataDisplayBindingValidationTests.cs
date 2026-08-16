using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// Real bug, found via Umbraco.Prism's own test suite once a Wayfinder release finally reached
/// a fix that made <c>Flatten()</c> correctly descend into <c>SummaryListComponent.Children</c>
/// (previously an accidental omission — see <see cref="Wayfinder.Extensions.ComponentExtensions.GetSubmittableInputs"/>'s
/// own remarks). <see cref="ServiceBlueprint.ValidateDataDisplayBindings"/> used to build its
/// "is this fieldKey known" set from every <c>InputComponent</c> in the tree — which, once
/// <c>Flatten()</c> was fixed, included each summary-list child's own fieldKey. That made the
/// check self-referential: a summary-list child bound to a fieldKey that resolves nowhere
/// (not a real input, not a calculated field) always found "itself" in the known-fields set,
/// since it put itself there — so a genuinely dangling binding was never flagged.
/// </summary>
public class ServiceBlueprintDataDisplayBindingValidationTests
{
    private static ServiceBlueprint Blueprint(params Component[] components) => new()
    {
        DefinitionKey = "test",
        DisplayName = "Test",
        InitialStage = "result",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "result",
                DisplayName = "Result",
                Components = components,
            },
        ],
    };

    [Fact]
    public void SummaryListChild_BoundToUndefinedField_ReturnsError()
    {
        var blueprint = Blueprint(new SummaryListComponent
        {
            Title = "Fee",
            Children = [new TextInputComponent { FieldKey = "fee", Label = "Fee" }],
        });

        var errors = blueprint.ValidateDataDisplayBindings();

        errors.Should().ContainSingle(d =>
            d.Code == "DATA_DISPLAY_UNKNOWN_FIELD" &&
            d.Message.Contains("'fee'") &&
            d.Path == "stages.result.components[0].children[0].fieldKey");
    }

    [Fact]
    public void SummaryListChild_BoundToACalculatedField_ReturnsNoErrors()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "result",
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField> { ["total"] = new() { Expr = "1" } },
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
                            Title = "Total",
                            Children = [new TextInputComponent { FieldKey = "total", Label = "Total" }],
                        },
                    ],
                },
            ],
        };

        blueprint.ValidateDataDisplayBindings().Should().BeEmpty();
    }

    [Fact]
    public void SummaryListChild_EchoingARealInputCapturedOnAnEarlierStage_ReturnsNoErrors()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "capture",
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "capture",
                    DisplayName = "Capture",
                    Components = [new TextInputComponent { FieldKey = "petName", Label = "Pet's name" }],
                },
                new StageDefinition
                {
                    StageKey = "result",
                    DisplayName = "Result",
                    Components =
                    [
                        new SummaryListComponent
                        {
                            Title = "Answers",
                            Children = [new TextInputComponent { FieldKey = "petName", Label = "Pet's name" }],
                        },
                    ],
                },
            ],
        };

        blueprint.ValidateDataDisplayBindings().Should().BeEmpty();
    }

    [Fact]
    public void SummaryListChild_BoundToAnotherSummaryListChildsOwnFieldKey_IsStillFlagged()
    {
        // Two summary-list echoes of the same undeclared name don't make each other legitimate —
        // neither resolves to a real input or a calculated field, so both should be flagged.
        var blueprint = Blueprint(new SummaryListComponent
        {
            Title = "Mutual",
            Children =
            [
                new TextInputComponent { FieldKey = "ghost", Label = "First" },
                new TextInputComponent { FieldKey = "ghost", Label = "Second" },
            ],
        });

        var errors = blueprint.ValidateDataDisplayBindings();

        errors.Where(d => d.Code == "DATA_DISPLAY_UNKNOWN_FIELD").Should().HaveCount(2);
    }

    [Fact]
    public void StatGroupItem_BoundToUndefinedField_ReturnsError()
    {
        var blueprint = Blueprint(new StatGroupComponent
        {
            Title = "Stats",
            Items = [new StatItemDefinition { Label = "Fee", FieldKey = "fee" }],
        });

        var errors = blueprint.ValidateDataDisplayBindings();

        errors.Should().ContainSingle(d =>
            d.Code == "DATA_DISPLAY_UNKNOWN_FIELD" &&
            d.Message.Contains("'fee'"));
    }

    [Fact]
    public void SummaryListChild_MissingFieldKey_ReturnsMissingFieldDiagnostic()
    {
        var blueprint = Blueprint(new SummaryListComponent
        {
            Title = "Fee",
            Children = [new TextInputComponent { FieldKey = "", Label = "Fee" }],
        });

        blueprint.ValidateDataDisplayBindings().Should().ContainSingle(d => d.Code == "DATA_DISPLAY_MISSING_FIELD");
    }

    [Fact]
    public void BulkDataReview_MissingDatasetIdField_ReturnsMissingFieldDiagnostic()
    {
        var blueprint = Blueprint(new BulkDataReviewComponent { Title = "Review", DatasetIdField = "" });

        blueprint.ValidateDataDisplayBindings().Should().ContainSingle(d => d.Code == "DATA_DISPLAY_MISSING_FIELD");
    }

    [Fact]
    public void BulkDataReview_BoundToUndefinedDatasetIdField_ReturnsError()
    {
        var blueprint = Blueprint(new BulkDataReviewComponent { Title = "Review", DatasetIdField = "ghostDatasetId" });

        blueprint.ValidateDataDisplayBindings().Should().ContainSingle(d =>
            d.Code == "DATA_DISPLAY_UNKNOWN_FIELD" && d.Message.Contains("'ghostDatasetId'"));
    }

    [Fact]
    public void BulkDataReview_BoundToAnIngestActionsDatasetIdField_ReturnsNoErrors()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "test",
            DisplayName = "Test",
            InitialStage = "review",
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "review",
                    DisplayName = "Review",
                    Components = [new BulkDataReviewComponent { Title = "Review", DatasetIdField = "contributionsDatasetId" }],
                    Actions =
                    [
                        new ActionDefinition
                        {
                            Type = Wayfinder.Models.ServiceDesign.BulkData.BulkDataActionTypes.BulkDatasetIngest,
                            Timing = "onEnter",
                            Parameters = new System.Text.Json.Nodes.JsonObject
                            {
                                ["sourceFileField"] = "contributionsResponseFile",
                                ["datasetIdField"] = "contributionsDatasetId",
                                ["columns"] = new System.Text.Json.Nodes.JsonArray
                                {
                                    new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey",
                                    },
                                },
                            },
                        },
                    ],
                },
            ],
        };

        blueprint.ValidateDataDisplayBindings().Should().NotContain(d =>
            d.Code == "DATA_DISPLAY_MISSING_FIELD" || d.Code == "DATA_DISPLAY_UNKNOWN_FIELD");
    }
}
