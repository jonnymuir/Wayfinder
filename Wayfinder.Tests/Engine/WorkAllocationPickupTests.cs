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
/// <c>ProcessManagerEngine.PickupWorkItem</c>/<c>PutbackWorkItem</c> — per-cursor pickup/ownership,
/// scoped to a cursor's dwell at its current node (see docs/guides/work-allocation.md). A pickup
/// hides the row entirely from every other actor sharing the queue, survives an in-place cursor
/// move (a "change:" jump or a plain stage-to-stage hop), and clears automatically the instant the
/// cursor is consumed by a Split or Join gateway crossing — deliberate, not a bug.
/// </summary>
public class WorkAllocationPickupTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "pickup-test";

    private static readonly ActorProfile SharedQueueProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    private static readonly ActorProfile OwnerRestrictedProfile = SharedQueueProfile with { RestrictToInstanceOwner = true };

    private static readonly ActorProfile AutomationProfile = new()
    {
        VisibleQueues = ["automation"],
        StartableQueues = [],
        ActionableQueues = ["automation"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "pickup-test",
          "displayName": "Pickup Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" },
            { "key": "automation", "displayName": "Automation", "actor": "system" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [
                { "id": "start--continue--middle", "target": "middle", "trigger": "continue" },
                { "id": "start--go-wait--split", "target": "to-automation", "trigger": "go-wait" }
              ]
            },
            {
              "stageKey": "middle",
              "displayName": "Middle",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes2", "label": "Notes", "required": false } ],
              "routes": [ { "id": "middle--finish--done", "target": "done", "trigger": "finish" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ]
            },
            {
              "stageKey": "in-review",
              "displayName": "In review",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "In review" } ],
              "routes": [ { "id": "in-review--approved--join", "target": "check-complete", "trigger": "approved" } ]
            },
            {
              "stageKey": "approved",
              "displayName": "Approved",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Approved" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-automation",
              "displayName": "Send to automation",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [
                { "id": "to-automation--go-wait--join", "target": "check-complete", "trigger": "go-wait" },
                { "id": "to-automation--go-wait--review", "target": "in-review", "trigger": "go-wait" }
              ]
            },
            {
              "key": "check-complete",
              "displayName": "Check complete",
              "gatewayType": "Join",
              "queueKey": "caseworker",
              "waitingContent": "Waiting.",
              "routes": [ { "id": "check-complete--approved--approved", "target": "approved", "trigger": "approved" } ],
              "requiredIncomingQueues": ["caseworker", "automation"]
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

    [Fact]
    public void PickingUpAZeroCursorInstance_MaterializesThePrimaryCursor_AndHidesItFromOtherActors()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        started.Render!.Should().NotBeNull("sanity check: a fresh instance has no cursors yet");

        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        pickedUp.ResponseState.Should().Be("render");

        var aliceView = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        aliceView.PickupState.Should().Be(QueueWorkItemPickupState.PickedUpByMe);

        engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "picked up by alice — hidden entirely from bob, not just non-actionable");

        var bobDirectView = engine.GetCurrent(DefinitionKey, TenantId, "bob", SharedQueueProfile, started.InstanceId);
        bobDirectView.ResponseState.Should().Be("error", "bob has no accessible work item on this instance at all once alice holds the pickup");
    }

    [Fact]
    public void PickedUpByOther_DirectAdvanceAttempt_StillFailsWithInvalidTransition()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobAdvance = engine.Advance(
            started.InstanceId, TenantId, "bob", SharedQueueProfile, "continue", pickedUp.StateVersion, null);

        bobAdvance.ResponseState.Should().Be("error");
        bobAdvance.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");
    }

    [Fact]
    public void SecondPickupAttempt_ByADifferentActor_ReturnsAlreadyPickedUp()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobPickup = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);

        bobPickup.ResponseState.Should().Be("error");
        bobPickup.Problems.Should().Contain(p => p.Code == "ALREADY_PICKED_UP");
    }

    [Fact]
    public void PutbackWorkItem_ReturnsItToThePool_VisibleToEveryoneAgain()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var putBack = engine.PutbackWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        putBack.ResponseState.Should().Be("render");
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.PickupState.Should().Be(QueueWorkItemPickupState.NotPickedUp);

        // Self-service only — bob (who never held it) can't put back someone else's pickup, but this
        // one is already not picked up by the time he'd try, so re-putting-it-back himself is a no-op.
        var bobPutbackAlreadyNotPickedUp = engine.PutbackWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);
        bobPutbackAlreadyNotPickedUp.ResponseState.Should().Be("render");
    }

    [Fact]
    public void PutbackWorkItem_ByANonOwner_WhileStillPickedUp_IsRejected()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobPutback = engine.PutbackWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);

        bobPutback.ResponseState.Should().Be("error");
        bobPutback.Problems.Should().Contain(p => p.Code == "ALREADY_PICKED_UP_BY_OTHER");
    }

    [Fact]
    public void Pickup_SurvivesAPlainStageToStageHop()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        // Picking up materialized Cursors.Count > 0, so this now goes through the multi-cursor
        // MoveCursor path (a `with` update), not the zero-cursor path — the mechanism wrinkle #2
        // in docs/guides/work-allocation.md depends on.
        var advanced = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "continue", pickedUp.StateVersion, null);
        advanced.Render!.StateDisplayName.Should().Be("Middle");

        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items;
        bobView.Should().NotContain(i => i.InstanceId == started.InstanceId, "the pickup must still be in effect after the plain hop");

        var aliceView = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        aliceView.PickupState.Should().Be(QueueWorkItemPickupState.PickedUpByMe);
        aliceView.StageKey.Should().Be("middle");
    }

    [Fact]
    public void Pickup_SurvivesAChangeLinkJump()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var jumped = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "change:middle", pickedUp.StateVersion, null);
        jumped.Render!.StateDisplayName.Should().Be("Middle");

        engine.GetQueueWorkItems("bob", SharedQueueProfile).Items
            .Should().NotContain(i => i.InstanceId == started.InstanceId, "the pickup must still be in effect after the change-link jump");
    }

    [Fact]
    public void Pickup_ClearsAutomatically_WhenTheCursorCrossesASplitGateway()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", pickedUp.StateVersion, null);
        afterSplit.ResponseState.Should().Be("defer", "alice's own cursor now waits at the join");

        // A brand-new RequestCursor was minted for the join wait — the pickup doesn't survive the
        // Split crossing, so bob can see it again (this is the deliberate design, not a bug).
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.PickupState.Should().BeNull("a Waiting row has nothing to pick up");
    }

    [Fact]
    public void Pickup_ClearsAutomatically_AfterAJoinReleases()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", pickedUp.StateVersion, null);

        // Directly advance the automation cursor to complete the join — no support-system-call
        // needed for this test, just a plain single-cursor advance under a different profile.
        // "In review" is a bare panel — InferStepType reads that as "confirmation" (ResponseState
        // "complete") regardless of it still having an outgoing route; a real, pre-existing engine
        // nuance (see IsTerminalInstance's own doc comment), not something this test is about.
        var automationStart = engine.GetCurrent(DefinitionKey, TenantId, "system", AutomationProfile, afterSplit.InstanceId);
        automationStart.Render!.StateDisplayName.Should().Be("In review");

        // "automation" declares no AssignmentPolicy — pickup is still mandatory (see
        // docs/guides/work-allocation.md), same as any other shared queue.
        var automationItem = engine.GetQueueWorkItems("system", AutomationProfile).Items.Single(i => i.InstanceId == afterSplit.InstanceId);
        var automationPickedUp = engine.PickupWorkItem(afterSplit.InstanceId, automationItem.CursorId, TenantId, "system", AutomationProfile);

        // The release moves the instance's primary visible position into the CASEWORKER queue's
        // "approved" stage — AutomationProfile (VisibleQueues: ["automation"] only) genuinely has
        // nothing left to see on this instance at all once that happens, so `released` itself
        // (built with AutomationProfile) correctly reports ACCESS_DENIED; check the release landed
        // via a caseworker-side view instead.
        var released = engine.Advance(
            afterSplit.InstanceId, TenantId, "system", AutomationProfile, "approved", automationPickedUp.StateVersion, null);
        released.ResponseState.Should().Be("error");
        released.Problems.Should().Contain(p => p.Code == "ACCESS_DENIED");

        var caseworkerView = engine.GetCurrent(DefinitionKey, TenantId, "bob", SharedQueueProfile, afterSplit.InstanceId);
        caseworkerView.Render!.StateDisplayName.Should().Be("Approved");

        // "Approved" is itself a bare panel (Done, per QueueWorkItemStatus) — request it explicitly.
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile, statuses: [QueueWorkItemStatus.Done]).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.PickupState.Should().BeNull("a Done row has nothing to pick up — no stale pickup carried forward either way");
    }

    [Fact]
    public void PickupNotAvailable_AWaitingRow_ReturnsPickupNotAvailable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", pickedUp.StateVersion, null);

        // The join gateway's own cursor id — fetch it from the queue list first.
        var waitingItem = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Single(i => i.InstanceId == afterSplit.InstanceId);
        var realAttempt = engine.PickupWorkItem(afterSplit.InstanceId, waitingItem.CursorId, TenantId, "alice", SharedQueueProfile);

        realAttempt.ResponseState.Should().Be("error");
        realAttempt.Problems.Should().Contain(p => p.Code == "PICKUP_NOT_AVAILABLE");
    }

    [Fact]
    public void PickupNotAvailable_ADoneRow_ReturnsPickupNotAvailable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        var afterMiddle = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "continue", pickedUp.StateVersion, null);
        var afterDone = engine.Advance(afterMiddle.InstanceId, TenantId, "alice", SharedQueueProfile, "finish", afterMiddle.StateVersion, null);
        afterDone.ResponseState.Should().Be("complete");

        var doneItem = engine.GetQueueWorkItems("alice", SharedQueueProfile, statuses: [QueueWorkItemStatus.Done]).Items
            .Single(i => i.InstanceId == started.InstanceId);

        var pickupAttempt = engine.PickupWorkItem(started.InstanceId, doneItem.CursorId, TenantId, "alice", SharedQueueProfile);

        pickupAttempt.ResponseState.Should().Be("error");
        pickupAttempt.Problems.Should().Contain(p => p.Code == "PICKUP_NOT_AVAILABLE");
    }

    [Fact]
    public void PickupNotAvailable_AnOwnerRestrictedProfile_ReturnsPickupNotAvailable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", OwnerRestrictedProfile);

        var pickupAttempt = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", OwnerRestrictedProfile);

        pickupAttempt.ResponseState.Should().Be("error");
        pickupAttempt.Problems.Should().Contain(p => p.Code == "PICKUP_NOT_AVAILABLE", "an owner-restricted instance already has exactly one possible actor — nothing to pick up");
    }

    [Fact]
    public void PickupWorkItem_OnAnUnknownInstance_ReturnsInstanceNotFound()
    {
        var engine = BuildEngine();

        var result = engine.PickupWorkItem("does-not-exist", RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "INSTANCE_NOT_FOUND");
    }
}
