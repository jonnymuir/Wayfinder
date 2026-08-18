using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <c>ProcessManagerEngine.ClaimNextAvailableWorkItem</c> — the automated/scaled-out "give me the
/// next thing" primitive (see docs/guides/work-allocation.md). Deliberately simple for v1: one
/// atomic claim, no lease/heartbeat/expiry.
/// </summary>
public class ClaimNextAvailableWorkItemTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "claim-next-test";

    private static readonly ActorProfile SharedQueueProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    private static readonly ActorProfile IneligibleProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
        Capabilities = new HashSet<string> { "some-other-capability" }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "claim-next-test",
          "displayName": "Claim Next Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker", "roleGates": ["worker"] }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "start--finish--done", "target": "done", "trigger": "finish" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ]
            }
          ]
        }
        """;

    private static ProcessManagerEngine BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    private static readonly ActorProfile EligibleProfile = SharedQueueProfile with
    {
        Capabilities = new HashSet<string> { "worker" }
    };

    [Fact]
    public void ReturnsTheOldestEligibleUnclaimedRow()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        Thread.Sleep(15);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        var claimed = engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        claimed.Should().NotBeNull();
        claimed!.InstanceId.Should().Be(first.InstanceId, "the older of the two instances must be claimed first");
        claimed.ClaimState.Should().Be(QueueWorkItemClaimState.ClaimedByMe);
        second.InstanceId.Should().NotBe(first.InstanceId, "sanity check: these really are two distinct instances");
    }

    [Fact]
    public void TwoRacingCallers_NeverClaimTheSameRow()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        var claimedByWorker1 = engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);
        var claimedByWorker2 = engine.ClaimNextAvailableWorkItem(TenantId, "worker-2", EligibleProfile);

        claimedByWorker1.Should().NotBeNull();
        claimedByWorker2.Should().NotBeNull();
        claimedByWorker1!.InstanceId.Should().NotBe(claimedByWorker2!.InstanceId);
        new[] { claimedByWorker1.InstanceId, claimedByWorker2.InstanceId }
            .Should().BeEquivalentTo([first.InstanceId, second.InstanceId]);
    }

    [Fact]
    public void ReturnsNull_WhenNothingIsClaimable()
    {
        var engine = BuildEngine();

        engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_OnceEverythingIsAlreadyClaimed()
    {
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        engine.ClaimNextAvailableWorkItem(TenantId, "worker-2", EligibleProfile).Should().BeNull();
    }

    [Fact]
    public void RespectsQueueEligibility_AnIneligibleProfileNeverClaimsAGatedQueuesRow()
    {
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", IneligibleProfile).Should().BeNull(
            "the queue declares roleGates: ['worker'], which IneligibleProfile doesn't hold");
    }

    [Fact]
    public void SkipsAnAlreadyClaimedRow_AndClaimsTheNextOldestInstead()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        Thread.Sleep(15);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        engine.ClaimWorkItem(first.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "already-claimed-by-someone", EligibleProfile);

        var claimed = engine.ClaimNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        claimed.Should().NotBeNull();
        claimed!.InstanceId.Should().Be(second.InstanceId, "the oldest one is already claimed, so the next-oldest must be picked instead");
    }
}
