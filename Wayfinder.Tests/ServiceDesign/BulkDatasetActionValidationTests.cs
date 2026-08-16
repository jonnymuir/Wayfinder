using System.Text.Json.Nodes;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// Covers <see cref="ServiceBlueprint.ValidateBulkDatasetActions"/> and the
/// <see cref="ServiceBlueprint.ValidateDataDisplayBindings"/> extension that recognises a
/// <c>bulk-dataset-ingest</c> action's declared count-output fields as legitimate summary-list/
/// stat-group bindings — the same "action resolution can legitimately populate a field no stage
/// ever captures" shape already covered for support-system actions
/// (<see cref="SupportSystemActionValidationTests"/>), unlike that feature there's no registry
/// here: a bulk dataset's shape is authored directly on the action, not looked up.
/// </summary>
public class BulkDatasetActionValidationTests
{
    private static JsonArray ValidColumns() => new()
    {
        new JsonObject { ["key"] = "memberRef", ["title"] = "Member reference", ["valueKind"] = "String", ["role"] = "RowKey" },
        new JsonObject { ["key"] = "memberName", ["title"] = "Name", ["valueKind"] = "String", ["role"] = "Data", ["editable"] = true },
        new JsonObject { ["key"] = "errorText", ["title"] = "Errors", ["valueKind"] = "String", ["role"] = "ResponseError" },
    };

    private static ActionDefinition MakeIngestAction(JsonObject? parameters = null) => new()
    {
        Type = BulkDataActionTypes.BulkDatasetIngest,
        Timing = "onEnter",
        Parameters = parameters ?? new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["errorCountField"] = "contributionsErrorCount",
            ["columns"] = ValidColumns(),
        },
    };

    private static ActionDefinition MakeMaterializeAction(JsonObject? parameters = null) => new()
    {
        Type = BulkDataActionTypes.BulkDatasetMaterialize,
        Timing = "onEnter",
        Parameters = parameters ?? new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["targetFileField"] = "contributionsFile",
        },
    };

    private static ServiceBlueprint MakeBlueprint(
        ActionDefinition ingestAction, ActionDefinition? materializeAction = null) => new()
    {
        DefinitionKey = "fixture",
        DisplayName = "Fixture",
        InitialStage = "upload",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "upload",
                DisplayName = "Upload",
                QueueKey = "citizen",
                Components = [new FileUploadComponent { FieldKey = "contributionsFile", Label = "File" }],
            },
            new StageDefinition
            {
                StageKey = "review",
                DisplayName = "Review",
                QueueKey = "caseworker",
                Components =
                [
                    new SummaryListComponent
                    {
                        Title = "Summary",
                        Children = [new TextInputComponent { FieldKey = "contributionsErrorCount", Label = "Errors" }],
                    },
                ],
                Actions = materializeAction is null ? [ingestAction] : [ingestAction, materializeAction],
            },
        ],
    };

    [Fact]
    public void ValidActions_ProduceNoDiagnostics()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(), MakeMaterializeAction());

        blueprint.ValidateBulkDatasetActions().Should().BeEmpty();
    }

    [Fact]
    public void MissingSourceField_Flagged()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject { ["columns"] = ValidColumns() }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_MISSING_SOURCE_FIELD");
    }

    [Fact]
    public void SourceFieldNotAKnownField_Flagged()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "notARealField",
            ["columns"] = ValidColumns(),
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_INVALID_SOURCE_FIELD");
    }

    [Fact]
    public void MissingColumns_Flagged()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_MISSING_COLUMNS");
    }

    [Fact]
    public void NoRowKeyColumn_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberName", ["title"] = "Name", ["valueKind"] = "String", ["role"] = "Data" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_MISSING_ROW_KEY");
    }

    [Fact]
    public void MoreThanOneRowKeyColumn_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey" },
            new JsonObject { ["key"] = "altRef", ["title"] = "Alt ref", ["valueKind"] = "String", ["role"] = "RowKey" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_DUPLICATE_ROW_KEY_ROLE");
    }

    [Fact]
    public void DuplicateColumnKey_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey" },
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref again", ["valueKind"] = "String", ["role"] = "Data" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().Contain(d => d.Code == "BULK_DATASET_ACTION_DUPLICATE_COLUMN_KEY");
    }

    [Fact]
    public void UnrecognisedRole_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey" },
            new JsonObject { ["key"] = "weird", ["title"] = "Weird", ["valueKind"] = "String", ["role"] = "NotARealRole" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_UNKNOWN_ROLE");
    }

    [Fact]
    public void UnrecognisedValueKind_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey" },
            new JsonObject { ["key"] = "weird", ["title"] = "Weird", ["valueKind"] = "NotARealKind", ["role"] = "Data" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_UNKNOWN_VALUE_KIND");
    }

    [Fact]
    public void ColumnMissingKeyOrTitle_Flagged()
    {
        var columns = new JsonArray
        {
            new JsonObject { ["key"] = "memberRef", ["title"] = "Ref", ["valueKind"] = "String", ["role"] = "RowKey" },
            new JsonObject { ["valueKind"] = "String", ["role"] = "Data" },
        };
        var blueprint = MakeBlueprint(MakeIngestAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
            ["columns"] = columns,
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_INVALID_COLUMN");
    }

    [Fact]
    public void Materialize_MissingTargetField_Flagged()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(), MakeMaterializeAction(new JsonObject
        {
            ["sourceFileField"] = "contributionsFile",
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_MISSING_TARGET_FIELD");
    }

    [Fact]
    public void Materialize_SourceFieldNotAnyIngestActions_Flagged()
    {
        var blueprint = MakeBlueprint(MakeIngestAction(), MakeMaterializeAction(new JsonObject
        {
            ["sourceFileField"] = "someOtherFile",
            ["targetFileField"] = "contributionsFile",
        }));

        blueprint.ValidateBulkDatasetActions().Should().ContainSingle(d => d.Code == "BULK_DATASET_ACTION_UNKNOWN_DATASET");
    }

    [Fact]
    public void DataDisplayBindings_RecognisesAnIngestActionsCountOutputAsAKnownField()
    {
        var blueprint = MakeBlueprint(MakeIngestAction());

        // "review"'s summary-list binds to contributionsErrorCount, which no stage ever captures
        // as an input — only legitimate because the bulk-dataset-ingest action's resolution
        // writes it.
        blueprint.ValidateDataDisplayBindings().Should().NotContain(d => d.Code == "DATA_DISPLAY_UNKNOWN_FIELD");
    }
}
