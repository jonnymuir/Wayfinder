using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Proves the real Wayfinder.ReferenceApp/service-blueprints/njf-contributions.json blueprint —
/// the worked example for docs/guides/bulk-data-review.md — both statically validates and
/// behaves correctly end to end: the review stage's "Accept and finish" route is only offered
/// once SafetyNet Underwriting's response has zero errors, and resubmitting genuinely feeds the
/// previously-ingested dataset (round 1's response), not the original upload, into SafetyNet's
/// second call. Mirrors JugglingLicenceStageValidationTests' pattern for the same reason: the
/// real seed file, not a synthetic fixture, is what a Playwright spec and an actual demo user
/// both depend on.
/// </summary>
public class NjfContributionsBlueprintTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string DefinitionKey = "njf-contributions";

    // Scoped the way a real caseworker's own profile would be (see
    // Wayfinder.ReferenceApp/Services/ReferenceActors.CaseworkerProfile) — restricted to the
    // caseworker queue, so the automation queue's own stage never competes as a visible "primary"
    // position the way it legitimately would under an unrestricted "god view" profile (see
    // SupportSystemActionExecutionTests' own remarks on the same thing). This is what actually
    // exercises the wait/poll experience instead of exposing the machine-only automation stage.
    private static readonly ActorProfile CaseworkerProfile = new()
    {
        // The real njf-contributions.json's own queue is "njf-team", not "caseworker" (see
        // docs/guides/team-assignment.md — it used to share that key with juggling-licence.json,
        // disambiguated only by RoleGates, but now gets its own distinct, team-scoped identity).
        VisibleQueues = ["njf-team"],
        StartableQueues = ["njf-team"],
        ActionableQueues = ["njf-team"],
        RestrictToInstanceOwner = false,
        // The real njf-contributions.json now gates its "njf-team" queue with
        // roleGates: ["njf-contributions-review"] (see docs/guides/work-allocation.md) — matches
        // Wayfinder.ReferenceApp/Services/ReferenceActors.NjfOperationsProfile's own capability.
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "njf-contributions-review" },
        // assign-to-initiator (see docs/guides/team-assignment.md) doesn't strictly need this to
        // function — establishment attributes ownership to whoever started it, not to team
        // membership — but matches ReferenceActors.NjfOperationsProfile's own real shape.
        TeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "njf-contributions-team" }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class UnusedStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static string SeedPath([CallerFilePath] string testFilePath = "") =>
        Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "Wayfinder.ReferenceApp", "service-blueprints", "njf-contributions.json");

    private static ServiceBlueprint LoadDefinition() =>
        JsonSerializer.Deserialize<ServiceBlueprint>(File.ReadAllText(SeedPath()), JsonOptions)!;

    // Mirrors Wayfinder.ReferenceApp/Services/SupportSystems/SafetyNetUnderwritingClient.cs's own
    // registration for this capability — this test project doesn't reference the reference app,
    // so it declares the same shape by hand, same as JugglingLicenceStageValidationTests already
    // does for the risk-assessment capability. Keep in sync if that descriptor changes.
    private static SupportSystemDescriptor SafetyNetDescriptor() => new()
    {
        Key = "safetynet-underwriting",
        DisplayName = "SafetyNet Underwriting",
        Capabilities =
        [
            new SupportSystemCapabilityDescriptor
            {
                Key = "validate-contributions-file",
                DisplayName = "Validate a contributions file",
                Inputs = [new() { Key = "file", Title = "File", ValueKind = ComponentPropertyValueKind.String, Required = true }],
                Outputs = [new() { Key = "contributionsResponseFile", Title = "Response file", ValueKind = ComponentPropertyValueKind.String }],
                SupportedCompletionModes = [SupportSystemCompletionMode.Poll],
                Outcomes = [new() { Key = "processed", DisplayName = "Processed" }],
            },
        ],
    };

    [Fact]
    public void RealBlueprint_ValidatesCleanly()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(SafetyNetDescriptor());

            var outcome = new ServiceBlueprintAuthoringService(new UnusedStore()).Validate(LoadDefinition());

            outcome.IsValid.Should().BeTrue(
                because: string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code} {d.Path}: {d.Message}")));
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    /// <summary>Same shape as BulkDatasetActionExecutionTests' scripted client, but wired to this real blueprint's actual field/capability names.</summary>
    private sealed class ScriptedClient(IServiceRequestFileStorage fileStorage) : ISupportSystemClient
    {
        private readonly Dictionary<string, string> _instanceIdByExternalRef = new();

        public string SupportSystemKey => "safetynet-underwriting";
        public bool ReadyToResolve { get; set; }
        public string NextResponseCsv { get; set; } = "";

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey, IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context, CancellationToken ct = default)
        {
            var externalReference = Guid.NewGuid().ToString("N");
            _instanceIdByExternalRef[externalReference] = context.InstanceId;
            return Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = externalReference });
        }

        public async Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey, SupportSystemInvocationReceipt receipt, CancellationToken ct = default)
        {
            if (!ReadyToResolve)
            {
                return null;
            }

            var instanceId = _instanceIdByExternalRef[receipt.ExternalReference];
            await using var responseStream = new MemoryStream(Encoding.UTF8.GetBytes(NextResponseCsv));
            var storageKey = await fileStorage.SaveAsync(instanceId, "contributionsResponseFile", responseStream, "response.csv", ct);
            var fileReference = new ServiceRequestFileReference
            {
                StorageKey = storageKey, OriginalFileName = "response.csv", ContentType = "text/csv", SizeBytes = NextResponseCsv.Length,
            };

            return new SupportSystemOutcome
            {
                OutcomeKey = "processed",
                ResultPayload = new JsonObject { ["contributionsResponseFile"] = JsonSerializer.SerializeToNode(fileReference) },
            };
        }
    }

    private const string Header = "memberRef,memberName,tier,fireEndorsement,under18,dob,monthlyContribution,safetyNetMemberId,errorText,warningText";

    private sealed record Session(ProcessManagerEngine Engine, ScriptedClient Client, string InstanceId);

    /// <summary>
    /// Shared setup both tests below need: a fresh engine wired exactly like
    /// Wayfinder.ReferenceApp/Program.cs's own registration, with an instance already through
    /// "submit" and its round-1 response scripted. Callers still own their own
    /// <c>SupportSystemRegistry.Register</c>/<c>ResetForTests</c> pairing, since xUnit runs
    /// [Fact]s in the same process and this registry is static.
    /// </summary>
    private static async Task<Session> StartAndSubmitAsync(string round1ResponseCsv)
    {
        var fileStorage = new InMemoryServiceRequestFileStorage();
        var bulkDatasetStore = new InMemoryBulkDatasetStore(fileStorage);
        var client = new ScriptedClient(fileStorage) { NextResponseCsv = round1ResponseCsv };

        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(LoadDefinition()),
            new PassthroughContentSanitizer(),
            // Mirrors Wayfinder.ReferenceApp/Program.cs's own ProcessManagerEngine
            // registration — contributionsErrorCount is "source: service" precisely so the
            // review stage's "accept"/"accept-with-warnings" routes' showWhen can see it;
            // without a resolver wired up, that's a CalculationException, not a
            // silently-missing value.
            serviceInputsResolver: (instance, definition, _) =>
                (definition.Calculations?.Fields ?? new Dictionary<string, Wayfinder.Models.ServiceDesign.Calculations.ServiceBlueprintCalculationField>())
                    .Where(field => string.Equals(field.Value.Source, "service", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(field => field.Key, field => instance.FieldValues.GetValueOrDefault(field.Key)),
            supportSystemClients: [client],
            bulkDatasetStore: bulkDatasetStore);

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var originalCsv = string.Join('\n', "memberRef,memberName,tier,fireEndorsement,under18,dob,monthlyContribution", "NJF-001,Alice,Recreational,N,N,,15.00");
        await using var originalStream = new MemoryStream(Encoding.UTF8.GetBytes(originalCsv));
        var originalStorageKey = await fileStorage.SaveAsync(started.InstanceId, "contributionsFile", originalStream, "contributions.csv");
        var originalFileReference = new ServiceRequestFileReference
        {
            StorageKey = originalStorageKey, OriginalFileName = "contributions.csv", ContentType = "text/csv", SizeBytes = originalCsv.Length,
        };

        var afterSubmit = engine.Advance(
            started.InstanceId, TenantId, UserId, CaseworkerProfile, "submit", started.StateVersion,
            new Dictionary<string, object?> { ["contributionsFile"] = originalFileReference });
        afterSubmit.ResponseState.Should().Be("defer");

        return new Session(engine, client, afterSubmit.InstanceId);
    }

    [Fact]
    public async Task FullLoop_AcceptNotOfferedUntilZeroErrors_ThenOfferedOnceRoundTwoIsClean()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(SafetyNetDescriptor());

            var session = await StartAndSubmitAsync(string.Join('\n',
                Header,
                "NJF-001,Alice,Recreational,N,N,,15.00,,,",
                "NJF-002,Bob,Recreational,N,N,,15.00,,Unrecognised tier,"));
            var (engine, client, instanceId) = session;

            client.ReadyToResolve = true;
            var atReview = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, instanceId);
            atReview.Render!.StateDisplayName.Should().Be("Review contributions file");

            var actionKeysRound1 = atReview.Render.AvailableActions.Select(a => a.ActionKey).ToArray();
            actionKeysRound1.Should().Contain("resubmit");
            actionKeysRound1.Should().NotContain("accept", "one row still has an error — Accept and finish must not be offered yet");

            // Tampering to submit "accept" anyway must be rejected, not silently accepted — same
            // protection ServiceBlueprintRouteDefinition.ShowWhen already gives elsewhere.
            var tampered = engine.Advance(atReview.InstanceId, TenantId, UserId, CaseworkerProfile, "accept", atReview.StateVersion, null);
            tampered.Problems.Should().ContainSingle(p => p.Code == "INVALID_TRANSITION");

            // Resubmit — bulk-dataset-materialize should feed SafetyNet round 1's ingested
            // response, and this round's response has no errors or warnings.
            client.ReadyToResolve = false;
            client.NextResponseCsv = string.Join('\n',
                Header,
                "NJF-001,Alice,Recreational,N,N,,15.00,,,",
                "NJF-002,Bob,Recreational,N,N,,15.00,,,");
            var afterResubmit = engine.Advance(atReview.InstanceId, TenantId, UserId, CaseworkerProfile, "resubmit", atReview.StateVersion, null);
            afterResubmit.ResponseState.Should().Be("defer");

            client.ReadyToResolve = true;
            var atReviewRound2 = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterResubmit.InstanceId);
            var actionKeysRound2 = atReviewRound2.Render!.AvailableActions.Select(a => a.ActionKey).ToArray();
            actionKeysRound2.Should().Contain("accept", "round 2's response has zero errors and zero warnings — Accept and finish must now be offered directly");
            actionKeysRound2.Should().NotContain("accept-with-warnings");

            var finished = engine.Advance(atReviewRound2.InstanceId, TenantId, UserId, CaseworkerProfile, "accept", atReviewRound2.StateVersion, null);
            finished.Problems.Should().BeEmpty();
            finished.Render!.StateDisplayName.Should().Be("Contributions file accepted");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public async Task ZeroErrorsWithAWarning_RequiresExplicitConfirmation_BeforeReachingDone()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(SafetyNetDescriptor());

            // Zero errors, one warning — the case the plain "accept" route deliberately excludes
            // (showWhen: "contributionsErrorCount = 0 and contributionsWarningCount = 0").
            var session = await StartAndSubmitAsync(string.Join('\n',
                Header,
                "NJF-001,Alice,Recreational,N,N,,15.00,,,",
                "NJF-002,Bob,Performer,N,N,,55.00,,,Contribution outside expected band"));
            var (engine, client, instanceId) = session;

            client.ReadyToResolve = true;
            var atReview = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, instanceId);
            var actionKeys = atReview.Render!.AvailableActions.Select(a => a.ActionKey).ToArray();
            actionKeys.Should().NotContain("accept", "a warning is present — the direct-finish route must not be offered");
            actionKeys.Should().Contain("accept-with-warnings", "zero errors with a warning present must offer the confirm-first route instead");

            // Tampering straight to "accept" (bypassing the confirmation stage entirely) must
            // still be rejected — the same protection as the zero-errors-with-errors case.
            var tampered = engine.Advance(atReview.InstanceId, TenantId, UserId, CaseworkerProfile, "accept", atReview.StateVersion, null);
            tampered.Problems.Should().ContainSingle(p => p.Code == "INVALID_TRANSITION");

            var atConfirm = engine.Advance(atReview.InstanceId, TenantId, UserId, CaseworkerProfile, "accept-with-warnings", atReview.StateVersion, null);
            atConfirm.Problems.Should().BeEmpty();
            atConfirm.Render!.StateDisplayName.Should().Be("Confirm before finishing");
            var confirmActionKeys = atConfirm.Render.AvailableActions.Select(a => a.ActionKey).ToArray();
            confirmActionKeys.Should().Contain("back-to-review");
            confirmActionKeys.Should().Contain("accept");

            // Changing your mind goes back to the same review stage — the already-ingested
            // dataset (idempotency-cached by stage/source file, per bulk-dataset-ingest's own
            // doc comment) must still be there, not re-parsed or lost.
            var backAtReview = engine.Advance(atConfirm.InstanceId, TenantId, UserId, CaseworkerProfile, "back-to-review", atConfirm.StateVersion, null);
            backAtReview.Render!.StateDisplayName.Should().Be("Review contributions file");
            backAtReview.Render.AvailableActions.Select(a => a.ActionKey).Should().Contain("accept-with-warnings");

            var backAtConfirm = engine.Advance(backAtReview.InstanceId, TenantId, UserId, CaseworkerProfile, "accept-with-warnings", backAtReview.StateVersion, null);
            var finished = engine.Advance(backAtConfirm.InstanceId, TenantId, UserId, CaseworkerProfile, "accept", backAtConfirm.StateVersion, null);
            finished.Problems.Should().BeEmpty();
            finished.Render!.StateDisplayName.Should().Be("Contributions file accepted");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
