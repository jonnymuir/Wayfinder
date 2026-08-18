using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.ServiceDesign;

/// <summary>
/// A real, pre-existing gap this session's work-allocation feature closes: `juggling-licence.json`
/// and `njf-contributions.json` both independently declare a queue literally named "caseworker" —
/// before either declared `roleGates`, Casey (juggling-licence) and Priya (NJF operations) could
/// each already see the *other's* blueprint's rows purely because the queue keys collided, with no
/// relationship to which blueprint either of them actually works on. See
/// docs/guides/work-allocation.md and Wayfinder.ReferenceApp/Services/ReferenceActors.cs, whose
/// `CaseworkerProfile`/`NjfOperationsProfile` this test's fixtures mirror exactly.
/// </summary>
public class CaseworkerQueueCrossVisibilityTests
{
    private const string TenantId = "reference";
    private const string CaseyUserId = "caseworker@example.test";
    private const string PriyaUserId = "njf-operations@example.test";

    private static readonly ActorProfile CaseyProfile = new()
    {
        VisibleQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "juggling-licence-review" }
    };

    private static readonly ActorProfile PriyaProfile = new()
    {
        VisibleQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "njf-contributions-review" }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string SeedPath(string fileName, [CallerFilePath] string testFilePath = "") =>
        Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "Wayfinder.ReferenceApp", "service-blueprints", fileName);

    private sealed class TwoDefinitionStore : IServiceBlueprintStore
    {
        public IReadOnlyDictionary<string, ServiceBlueprint> LoadDefinitions(ILogger logger)
        {
            var jugglingLicence = JsonSerializer.Deserialize<ServiceBlueprint>(
                File.ReadAllText(SeedPath("juggling-licence.json")), JsonOptions)!;
            var njfContributions = JsonSerializer.Deserialize<ServiceBlueprint>(
                File.ReadAllText(SeedPath("njf-contributions.json")), JsonOptions)!;
            return new Dictionary<string, ServiceBlueprint>(StringComparer.OrdinalIgnoreCase)
            {
                [jugglingLicence.DefinitionKey] = jugglingLicence,
                [njfContributions.DefinitionKey] = njfContributions
            };
        }
    }

    private static ProcessManagerEngine BuildEngine() => new(
        NullLogger.Instance,
        new TwoDefinitionStore(),
        new PassthroughContentSanitizer());

    /// <summary>
    /// Places a fresh juggling-licence instance directly at "under-review" (the caseworker
    /// queue's own first stage) via the admin "change:" jump under an unrestricted profile —
    /// deliberately bypassing the citizen journey's own field-by-field validation, which this
    /// test has no need to exercise (see citizen-journey.spec.ts / JugglingLicenceStageValidationTests
    /// for that coverage). Mirrors the same "change:" jump mechanism GetCurrentOrStartFreshTests
    /// already relies on for the identical reason.
    /// </summary>
    private static string StartJugglingLicenceAtUnderReview(ProcessManagerEngine engine)
    {
        var started = engine.GetCurrent("juggling-licence", TenantId, "some-applicant", ActorProfile.UnrestrictedOwner);
        var jumped = engine.Advance(
            started.InstanceId, TenantId, "some-applicant", ActorProfile.UnrestrictedOwner,
            "change:under-review", started.StateVersion, null);
        jumped.Render!.StateDisplayName.Should().Be("Review application", "sanity check: the jump actually landed on the caseworker's own stage");
        return started.InstanceId;
    }

    [Fact]
    public void CaseyCanSeeHerOwnJugglingLicenceRow_ButNotPriyasNjfContributionsRow()
    {
        var engine = BuildEngine();
        StartJugglingLicenceAtUnderReview(engine);
        var njfInstance = engine.GetCurrent("njf-contributions", TenantId, PriyaUserId, PriyaProfile);

        var caseyView = engine.GetQueueWorkItems(CaseyUserId, CaseyProfile).Items;

        caseyView.Select(i => i.BlueprintKey).Should().Contain("juggling-licence");
        caseyView.Should().NotContain(i => i.BlueprintKey == "njf-contributions",
            "Casey holds 'juggling-licence-review', not 'njf-contributions-review' — the NJF row must be invisible to her, not merely non-actionable");
    }

    [Fact]
    public void PriyaCanSeeHerOwnNjfContributionsRow_ButNotCaseysJugglingLicenceRow()
    {
        var engine = BuildEngine();
        StartJugglingLicenceAtUnderReview(engine);
        engine.GetCurrent("njf-contributions", TenantId, PriyaUserId, PriyaProfile);

        var priyaView = engine.GetQueueWorkItems(PriyaUserId, PriyaProfile).Items;

        priyaView.Select(i => i.BlueprintKey).Should().Contain("njf-contributions");
        priyaView.Should().NotContain(i => i.BlueprintKey == "juggling-licence",
            "Priya holds 'njf-contributions-review', not 'juggling-licence-review' — the juggling-licence row must be invisible to her too");
    }

    [Fact]
    public void GetCurrent_DirectlyByInstanceId_AlsoRespectsQueueEligibility_NotJustTheListView()
    {
        var engine = BuildEngine();
        var njfInstance = engine.GetCurrent("njf-contributions", TenantId, PriyaUserId, PriyaProfile);

        var caseyDirectAttempt = engine.GetCurrent(
            "njf-contributions", TenantId, CaseyUserId, CaseyProfile, njfInstance.InstanceId);

        caseyDirectAttempt.ResponseState.Should().Be("error",
            "eligibility is enforced at FindAccessibleWorkItems itself, not just the worklist's own filtering");
    }
}
