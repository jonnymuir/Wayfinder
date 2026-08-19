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
/// <c>ProcessManagerEngine.PickupNextAvailableWorkItem</c> — the automated/scaled-out "give me the
/// next thing" primitive (see docs/guides/work-allocation.md). Deliberately simple for v1: one
/// atomic pickup, no lease/heartbeat/expiry.
/// </summary>
public class PickupNextAvailableWorkItemTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "pickup-next-test";

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
          "definitionKey": "pickup-next-test",
          "displayName": "Pickup Next Test",
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
    public void ReturnsTheOldestEligibleNotPickedUpRow()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        Thread.Sleep(15);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        var pickedUp = engine.PickupNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        pickedUp.Should().NotBeNull();
        pickedUp!.InstanceId.Should().Be(first.InstanceId, "the older of the two instances must be picked up first");
        pickedUp.PickupState.Should().Be(QueueWorkItemPickupState.PickedUpByMe);
        second.InstanceId.Should().NotBe(first.InstanceId, "sanity check: these really are two distinct instances");
    }

    [Fact]
    public void TwoRacingCallers_NeverPickUpTheSameRow()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        var pickedUpByWorker1 = engine.PickupNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);
        var pickedUpByWorker2 = engine.PickupNextAvailableWorkItem(TenantId, "worker-2", EligibleProfile);

        pickedUpByWorker1.Should().NotBeNull();
        pickedUpByWorker2.Should().NotBeNull();
        pickedUpByWorker1!.InstanceId.Should().NotBe(pickedUpByWorker2!.InstanceId);
        new[] { pickedUpByWorker1.InstanceId, pickedUpByWorker2.InstanceId }
            .Should().BeEquivalentTo([first.InstanceId, second.InstanceId]);
    }

    [Fact]
    public void ReturnsNull_WhenNothingIsAvailableToPickUp()
    {
        var engine = BuildEngine();

        engine.PickupNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_OnceEverythingIsAlreadyPickedUp()
    {
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        engine.PickupNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        engine.PickupNextAvailableWorkItem(TenantId, "worker-2", EligibleProfile).Should().BeNull();
    }

    [Fact]
    public void RespectsQueueEligibility_AnIneligibleProfileNeverPicksUpAGatedQueuesRow()
    {
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        engine.PickupNextAvailableWorkItem(TenantId, "worker-1", IneligibleProfile).Should().BeNull(
            "the queue declares roleGates: ['worker'], which IneligibleProfile doesn't hold");
    }

    [Fact]
    public void SkipsAnAlreadyPickedUpRow_AndPicksUpTheNextOldestInstead()
    {
        var engine = BuildEngine();
        var first = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);
        Thread.Sleep(15);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "creator", EligibleProfile);

        engine.PickupWorkItem(first.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "already-picked-up-by-someone", EligibleProfile);

        var pickedUp = engine.PickupNextAvailableWorkItem(TenantId, "worker-1", EligibleProfile);

        pickedUp.Should().NotBeNull();
        pickedUp!.InstanceId.Should().Be(second.InstanceId, "the oldest one is already picked up, so the next-oldest must be picked instead");
    }
}
