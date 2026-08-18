using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="IRequestConcurrencyPolicy"/> — the escape hatch for a concurrency rule
/// <see cref="ActorProfile.ConcurrencyScopeKey"/> can't express. Registered per blueprint key,
/// mirroring <see cref="Wayfinder.Engine.Abstractions.ISupportSystemClient"/>'s own per-key
/// registry shape; a blueprint with nothing registered for it is provably untouched.
/// </summary>
public class RequestConcurrencyPolicyExecutionTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string GovernedKey = "governed";
    private const string UngovernedKey = "ungoverned";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string BlueprintJson(string definitionKey) => $$"""
        {
          "definitionKey": "{{definitionKey}}",
          "displayName": "Concurrency Policy Test",
          "version": 1,
          "initialStage": "start",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ]
            }
          ]
        }
        """;

    private sealed class MultiDefinitionStore(params ServiceBlueprint[] definitions) : IServiceBlueprintStore
    {
        public IReadOnlyDictionary<string, ServiceBlueprint> LoadDefinitions(Microsoft.Extensions.Logging.ILogger logger) =>
            definitions.ToDictionary(d => d.DefinitionKey, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ScriptedPolicy(string definitionKey, RequestConcurrencyDecision decision) : IRequestConcurrencyPolicy
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> DefinitionKeys { get; } = [definitionKey];

        public Task<RequestConcurrencyDecision> EvaluateAsync(
            ServiceBlueprint definition, string tenantId, string userId, ActorProfile accessProfile,
            IReadOnlyList<ServiceRequest> candidateInstances, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(decision);
        }
    }

    private static ProcessManagerEngine BuildEngine(IRequestConcurrencyPolicy policy)
    {
        var governed = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson(GovernedKey), JsonOptions)!;
        var ungoverned = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson(UngovernedKey), JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new MultiDefinitionStore(governed, ungoverned),
            new PassthroughContentSanitizer(),
            requestConcurrencyPolicies: [policy]);
    }

    [Fact]
    public void AllowNew_CreatesAFreshInstance_AndIsConsultedOnlyForItsOwnBlueprint()
    {
        var policy = new ScriptedPolicy(GovernedKey, RequestConcurrencyDecision.AllowNew());
        var engine = BuildEngine(policy);

        var governedResult = engine.GetCurrent(GovernedKey, TenantId, UserId, ActorProfile.UnrestrictedOwner);
        var ungovernedResult = engine.GetCurrent(UngovernedKey, TenantId, UserId, ActorProfile.UnrestrictedOwner);

        governedResult.ResponseState.Should().Be("render");
        policy.CallCount.Should().Be(1, "only the governed blueprint's GetCurrent call should have consulted it");
        ungovernedResult.ResponseState.Should().Be("render", "the other blueprint falls through to the built-in policy, untouched");
    }

    [Fact]
    public void ReuseExisting_ReturnsTheNamedInstance_NotACreatedOne()
    {
        var precreated = new ServiceRequest
        {
            InstanceId = "precreated-instance",
            BlueprintKey = GovernedKey,
            TenantId = TenantId,
            UserId = "someone-else",
            ConcurrencyScopeKey = "someone-else",
            CurrentStage = "start",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var policy = new ScriptedPolicy(GovernedKey, RequestConcurrencyDecision.ReuseExisting(precreated));
        var engine = BuildEngine(policy);
        engine.GetAllInstances(); // no-op touch to keep the store reference obvious in this test's intent

        // The scripted policy always returns the same pre-created instance regardless of who's
        // asking or what candidates exist — proves the engine trusts the policy's own decision
        // rather than re-deriving one itself.
        var result = engine.GetCurrent(GovernedKey, TenantId, "a-completely-different-user", ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().Be("precreated-instance");
    }

    [Fact]
    public void Deny_SurfacesAsAnErrorEnvelope_NotARenderOrANewInstance()
    {
        var policy = new ScriptedPolicy(GovernedKey, RequestConcurrencyDecision.Deny("No new submissions during month-end close."));
        var engine = BuildEngine(policy);

        var result = engine.GetCurrent(GovernedKey, TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.ResponseState.Should().Be("error");
        result.Problems.Should().ContainSingle(p =>
            p.Code == "CONCURRENCY_POLICY_DENIED" && p.Message.Contains("month-end close"));
    }
}
