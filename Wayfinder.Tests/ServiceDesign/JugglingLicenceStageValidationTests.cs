using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
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
        var service = new ServiceBlueprintAuthoringService(new UnusedStore());

        var outcome = service.Validate(LoadDefinition());

        outcome.IsValid.Should().BeTrue(because: string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code} {d.Path}: {d.Message}")));
        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("STAGE_VALIDATION_"));

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
}
