using System.Text.Json.Nodes;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// Covers <see cref="ServiceBlueprint.ValidateSupportSystemActions"/> and the
/// <see cref="ServiceBlueprint.ValidateDataDisplayBindings"/> extension that recognises a
/// capability's declared <see cref="SupportSystemCapabilityDescriptor.Outputs"/> as legitimate
/// summary-list/stat-group field bindings — the gap found authoring the real
/// juggling-licence.json "send to insurer" flow (a summary-list bound to a field only ever
/// populated by a support-system-call action's resolution had nowhere to declare that
/// provenance).
/// </summary>
public class SupportSystemActionValidationTests
{
    private const string SupportSystemKey = "safetynet-underwriting";
    private const string CapabilityKey = "validate-risk-assessment";

    private static SupportSystemDescriptor FixtureDescriptor() => new()
    {
        Key = SupportSystemKey,
        DisplayName = "SafetyNet Underwriting",
        Capabilities =
        [
            new SupportSystemCapabilityDescriptor
            {
                Key = CapabilityKey,
                DisplayName = "Validate a risk assessment",
                Inputs =
                [
                    new() { Key = "File", Title = "File", ValueKind = ComponentPropertyValueKind.String, Required = true },
                    new() { Key = "Notes", Title = "Notes", ValueKind = ComponentPropertyValueKind.String },
                ],
                Outputs =
                [
                    new() { Key = "insurerDecisionNotes", Title = "Insurer decision notes", ValueKind = ComponentPropertyValueKind.String },
                ],
                SupportedCompletionModes = [SupportSystemCompletionMode.Poll],
                Outcomes = [new() { Key = "approved", DisplayName = "Approved" }, new() { Key = "rejected", DisplayName = "Rejected" }],
            },
        ],
    };

    private static ActionDefinition MakeAction(JsonObject? parameters = null) => new()
    {
        Type = SupportSystemActionTypes.SupportSystemCall,
        Timing = "onEnter",
        Parameters = parameters ?? new JsonObject
        {
            ["supportSystemKey"] = SupportSystemKey,
            ["capabilityKey"] = CapabilityKey,
            ["inputs"] = new JsonObject { ["File"] = "riskAssessment" },
        },
    };

    private static ServiceBlueprint MakeBlueprint(ActionDefinition action, IReadOnlyList<ServiceBlueprintRouteDefinition>? routes = null) => new()
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
                Components = [new FileUploadComponent { FieldKey = "riskAssessment", Label = "File" }],
            },
            new StageDefinition
            {
                StageKey = "automation-stage",
                DisplayName = "Automation",
                QueueKey = "automation",
                Components = [new PanelComponent { Heading = "Waiting" }],
                Actions = [action],
                Routes = routes ??
                [
                    new ServiceBlueprintRouteDefinition { Id = "r1", Target = "done", Trigger = "approved" },
                    new ServiceBlueprintRouteDefinition { Id = "r2", Target = "done", Trigger = "rejected" },
                ],
            },
            new StageDefinition
            {
                StageKey = "done",
                DisplayName = "Done",
                QueueKey = "citizen",
                Components =
                [
                    new SummaryListComponent
                    {
                        Title = "Summary",
                        Children = [new TextInputComponent { FieldKey = "insurerDecisionNotes", Label = "Notes" }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void ValidAction_ProducesNoDiagnostics()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction());

            blueprint.ValidateSupportSystemActions().Should().BeEmpty();
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void MissingSupportSystemOrCapabilityKey_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(new JsonObject { ["inputs"] = new JsonObject() }));

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_MISSING_KEYS");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void UnregisteredSupportSystem_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            var blueprint = MakeBlueprint(MakeAction());

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_UNKNOWN_SUPPORT_SYSTEM");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void UnregisteredCapability_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(new JsonObject
            {
                ["supportSystemKey"] = SupportSystemKey,
                ["capabilityKey"] = "not-a-real-capability",
                ["inputs"] = new JsonObject { ["File"] = "riskAssessment" },
            }));

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_UNKNOWN_CAPABILITY");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void MissingRequiredInputBinding_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(new JsonObject
            {
                ["supportSystemKey"] = SupportSystemKey,
                ["capabilityKey"] = CapabilityKey,
                ["inputs"] = new JsonObject(), // "File" is required but unbound
            }));

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_MISSING_REQUIRED_INPUT");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void UnknownInputMappingKey_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(new JsonObject
            {
                ["supportSystemKey"] = SupportSystemKey,
                ["capabilityKey"] = CapabilityKey,
                ["inputs"] = new JsonObject { ["File"] = "riskAssessment", ["NotARealInput"] = "riskAssessment" },
            }));

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_UNKNOWN_INPUT");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void InputBoundToNonexistentField_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(new JsonObject
            {
                ["supportSystemKey"] = SupportSystemKey,
                ["capabilityKey"] = CapabilityKey,
                ["inputs"] = new JsonObject { ["File"] = "notARealField" },
            }));

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_INPUT_UNKNOWN_FIELD");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void RouteTriggerNotAmongDeclaredOutcomes_Flagged()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction(), routes:
            [
                new ServiceBlueprintRouteDefinition { Id = "r1", Target = "done", Trigger = "maybe" },
            ]);

            blueprint.ValidateSupportSystemActions().Should().ContainSingle(d => d.Code == "SUPPORT_SYSTEM_ACTION_ROUTE_TRIGGER_UNKNOWN_OUTCOME");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void DataDisplayBindings_RecognisesACapabilitysDeclaredOutputAsAKnownField()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());
            var blueprint = MakeBlueprint(MakeAction());

            // "done"'s summary-list binds to insurerDecisionNotes, which no stage ever captures as
            // an input — only legitimate because the automation stage's action resolves it.
            blueprint.ValidateDataDisplayBindings().Should().NotContain(d => d.Code == "DATA_DISPLAY_UNKNOWN_FIELD");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void DataDisplayBindings_StillFlagsATrulyUnknownField_WhenNoSupportSystemDeclaresIt()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            // No registration at all — the binding has no possible provenance.
            var blueprint = MakeBlueprint(MakeAction());

            blueprint.ValidateDataDisplayBindings().Should().Contain(d => d.Code == "DATA_DISPLAY_UNKNOWN_FIELD");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
