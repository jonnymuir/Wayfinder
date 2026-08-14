using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// Proves the worked "risk-mitigation-evidence-required" example added to
/// Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json — a real, cross-stage
/// StageDefinition.Validations rule (the gate field, hasDangerousProps, is captured on
/// "event-details"; the rule itself lives on "risk-assessment") — both statically validates
/// clean and behaves correctly end to end through ProcessManagerEngine.Advance.
/// </summary>
public class JugglingLicenceStageValidationTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class UnusedStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static string JugglingLicenceSeedPath([CallerFilePath] string testFilePath = "") =>
        Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..",
            "Wayfinder.ReferenceApp", "service-blueprints", "juggling-licence.json");

    private static ServiceBlueprint LoadDefinition() =>
        JsonSerializer.Deserialize<ServiceBlueprint>(File.ReadAllText(JugglingLicenceSeedPath()), JsonOptions)!;

    [Fact]
    public void RealBlueprint_ValidatesCleanlyIncludingTheStageValidationRule()
    {
        // The blueprint's insurer-validation stage references "safetynet-underwriting" — real in
        // Wayfinder.ReferenceApp (Services/SupportSystems/SafetyNetUnderwritingClient.cs), which
        // this test project doesn't reference. Mirror its shape here so validation sees the same
        // registered support system production does; keep in sync if that descriptor changes.
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(new SupportSystemDescriptor
            {
                Key = "safetynet-underwriting",
                DisplayName = "SafetyNet Underwriting",
                Capabilities =
                [
                    new SupportSystemCapabilityDescriptor
                    {
                        Key = "validate-risk-assessment",
                        DisplayName = "Validate a risk assessment",
                        Inputs =
                        [
                            new() { Key = "file", Title = "File", ValueKind = ComponentPropertyValueKind.String, Required = true },
                            new() { Key = "applicantName", Title = "Applicant name", ValueKind = ComponentPropertyValueKind.String },
                            new() { Key = "eventName", Title = "Event name", ValueKind = ComponentPropertyValueKind.String },
                            new() { Key = "notes", Title = "Notes", ValueKind = ComponentPropertyValueKind.String },
                        ],
                        Outputs =
                        [
                            new() { Key = "insurerDecision", Title = "Insurer decision", ValueKind = ComponentPropertyValueKind.String },
                            new() { Key = "insurerDecisionNotes", Title = "Insurer decision notes", ValueKind = ComponentPropertyValueKind.String },
                        ],
                        SupportedCompletionModes = [SupportSystemCompletionMode.Poll, SupportSystemCompletionMode.Webhook],
                        Outcomes = [new() { Key = "approved", DisplayName = "Approved" }, new() { Key = "rejected", DisplayName = "Rejected" }],
                    },
                ],
            });

            var service = new ServiceBlueprintAuthoringService(new UnusedStore());

            var outcome = service.Validate(LoadDefinition());

            outcome.IsValid.Should().BeTrue(because: string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code} {d.Path}: {d.Message}")));
            outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("STAGE_VALIDATION_"));
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }

        var stage = LoadDefinition().Stages.Single(s => s.StageKey == "risk-assessment");
        stage.Validations.Should().ContainSingle(r => r.Code == "risk-mitigation-evidence-required");
    }

    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) ArriveAtRiskAssessment(bool hasDangerousProps)
    {
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(LoadDefinition()),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrent("juggling-licence", TenantId, UserId);

        var atEventDetails = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion,
            new Dictionary<string, object?> { ["applicantName"] = "Alice", ["applicantEmail"] = "alice@example.com" });
        Assert.Equal("About the event", atEventDetails.Render?.StateDisplayName);

        var atRiskAssessment = engine.Advance(
            atEventDetails.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", atEventDetails.StateVersion,
            new Dictionary<string, object?>
            {
                ["eventName"] = "Fire festival",
                ["eventDate"] = "2027-06-01",
                ["jugglerCount"] = 5,
                ["hasDangerousProps"] = hasDangerousProps,
            });
        Assert.Equal("Risk assessment", atRiskAssessment.Render?.StateDisplayName);

        return (engine, started.InstanceId, atRiskAssessment.StateVersion);
    }

    [Fact]
    public void Advance_BlocksDangerousActWithNoMitigationEvidence()
    {
        var (engine, instanceId, stateVersion) = ArriveAtRiskAssessment(hasDangerousProps: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion,
            new Dictionary<string, object?> { ["riskMitigationNotes"] = "We will be careful." });

        Assert.Contains(result.Problems, p => p.FieldKey == "riskMitigationNotes" && p.Code == "risk-mitigation-evidence-required");
        Assert.Equal("Risk assessment", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_AllowsDangerousActWithMeasurableMitigationEvidence()
    {
        var (engine, instanceId, stateVersion) = ArriveAtRiskAssessment(hasDangerousProps: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion,
            new Dictionary<string, object?> { ["riskMitigationNotes"] = "10 metres safety distance maintained throughout." });

        Assert.Empty(result.Problems);
        Assert.Equal("Check your answers and declare", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_SkipsRuleWhenActIsNotDangerous()
    {
        var (engine, instanceId, stateVersion) = ArriveAtRiskAssessment(hasDangerousProps: false);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion,
            new Dictionary<string, object?>());

        Assert.Empty(result.Problems);
        Assert.Equal("Check your answers and declare", result.Render?.StateDisplayName);
    }

    /// <summary>
    /// The action-scoped half of StageDefinition.Validations
    /// (<see cref="ServiceBlueprintStageValidationRule.Actions"/>), exercised against the real
    /// juggling-licence "under-review" stage: once an applicant has attached a risk assessment,
    /// the caseworker must send it to the insurer — "continue to decision" is blocked — while the
    /// very action the rule exists to force stays available. An unscoped rule could not express
    /// this: it would block send-to-insurer too.
    /// </summary>
    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) ArriveAtCaseworkerReview(bool withFile)
    {
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(LoadDefinition()),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrent("juggling-licence", TenantId, UserId);
        var atEventDetails = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "continue", started.StateVersion,
            new Dictionary<string, object?> { ["applicantName"] = "Alice", ["applicantEmail"] = "alice@example.com" });

        var atRiskAssessment = engine.Advance(
            atEventDetails.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "continue", atEventDetails.StateVersion,
            new Dictionary<string, object?>
            {
                ["eventName"] = "Fire festival",
                ["eventDate"] = "2027-06-01",
                ["jugglerCount"] = 5,
                ["hasDangerousProps"] = withFile,
            });

        var riskValues = new Dictionary<string, object?>
        {
            ["riskMitigationNotes"] = "10 metres safety distance maintained throughout.",
        };
        if (withFile)
        {
            riskValues["riskAssessment"] = new ServiceRequestFileReference
            {
                StorageKey = "memory://demo",
                OriginalFileName = "risk-assessment.pdf",
                ContentType = "application/pdf",
                SizeBytes = 128,
            };
        }

        var atDeclaration = engine.Advance(
            atRiskAssessment.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", atRiskAssessment.StateVersion, riskValues);

        var afterSubmit = engine.Advance(
            atDeclaration.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "submit", atDeclaration.StateVersion,
            new Dictionary<string, object?> { ["declarationConfirmed"] = true });

        return (engine, started.InstanceId, afterSubmit.StateVersion);
    }

    [Fact]
    public void Advance_BlocksContinueToDecisionWhenAFileMustGoToTheInsurer()
    {
        var (engine, instanceId, stateVersion) = ArriveAtCaseworkerReview(withFile: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "continue", stateVersion, null);

        Assert.Contains(result.Problems, p => p.Code == "insurer-check-required");
    }

    [Fact]
    public void Advance_StillAllowsSendingToTheInsurer_TheActionTheRuleExistsToForce()
    {
        // The whole point of scoping the rule to "continue": an unscoped rule would block this
        // too, making the required action impossible and the stage a dead end.
        var (engine, instanceId, stateVersion) = ArriveAtCaseworkerReview(withFile: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "send-to-insurer", stateVersion, null);

        Assert.DoesNotContain(result.Problems, p => p.Code == "insurer-check-required");
    }

    [Fact]
    public void Advance_AllowsContinueToDecisionWhenThereIsNoFileToSend()
    {
        var (engine, instanceId, stateVersion) = ArriveAtCaseworkerReview(withFile: false);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "continue", stateVersion, null);

        Assert.Empty(result.Problems);
        Assert.Equal("Record your decision", result.Render?.StateDisplayName);
    }
}
