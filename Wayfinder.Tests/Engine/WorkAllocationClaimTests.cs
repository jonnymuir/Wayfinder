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
/// <c>ProcessManagerEngine.ClaimWorkItem</c>/<c>ReleaseWorkItem</c> — per-cursor claim/ownership,
/// scoped to a cursor's dwell at its current node (see docs/guides/work-allocation.md). A claim
/// hides the row entirely from every other actor sharing the queue, survives an in-place cursor
/// move (a "change:" jump or a plain stage-to-stage hop), and clears automatically the instant the
/// cursor is consumed by a Split or Join gateway crossing — deliberate, not a bug.
/// </summary>
public class WorkAllocationClaimTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "claim-test";

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
          "definitionKey": "claim-test",
          "displayName": "Claim Test",
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
    public void ClaimingAZeroCursorInstance_MaterializesThePrimaryCursor_AndHidesItFromOtherActors()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        started.Render!.Should().NotBeNull("sanity check: a fresh instance has no cursors yet");

        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        claimed.ResponseState.Should().Be("render");

        var aliceView = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        aliceView.ClaimState.Should().Be(QueueWorkItemClaimState.ClaimedByMe);

        engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "claimed by alice — hidden entirely from bob, not just non-actionable");

        var bobDirectView = engine.GetCurrent(DefinitionKey, TenantId, "bob", SharedQueueProfile, started.InstanceId);
        bobDirectView.ResponseState.Should().Be("error", "bob has no accessible work item on this instance at all once alice holds the claim");
    }

    [Fact]
    public void ClaimedByOther_DirectAdvanceAttempt_StillFailsWithInvalidTransition()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobAdvance = engine.Advance(
            started.InstanceId, TenantId, "bob", SharedQueueProfile, "continue", claimed.StateVersion, null);

        bobAdvance.ResponseState.Should().Be("error");
        bobAdvance.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");
    }

    [Fact]
    public void SecondClaimAttempt_ByADifferentActor_ReturnsAlreadyClaimed()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobClaim = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);

        bobClaim.ResponseState.Should().Be("error");
        bobClaim.Problems.Should().Contain(p => p.Code == "ALREADY_CLAIMED");
    }

    [Fact]
    public void ReleaseWorkItem_ReturnsItToThePool_VisibleToEveryoneAgain()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var released = engine.ReleaseWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        released.ResponseState.Should().Be("render");
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.ClaimState.Should().Be(QueueWorkItemClaimState.Unclaimed);

        // Self-service only — bob (who never held it) can't release someone else's claim, but this
        // one is already unclaimed by the time he'd try, so re-releasing it himself is a no-op.
        var bobReleaseAlreadyUnclaimed = engine.ReleaseWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);
        bobReleaseAlreadyUnclaimed.ResponseState.Should().Be("render");
    }

    [Fact]
    public void ReleaseWorkItem_ByANonOwner_WhileStillClaimed_IsRejected()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var bobRelease = engine.ReleaseWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "bob", SharedQueueProfile);

        bobRelease.ResponseState.Should().Be("error");
        bobRelease.Problems.Should().Contain(p => p.Code == "ALREADY_CLAIMED_BY_OTHER");
    }

    [Fact]
    public void Claim_SurvivesAPlainStageToStageHop()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        // Claiming materialized Cursors.Count > 0, so this now goes through the multi-cursor
        // MoveCursor path (a `with` update), not the zero-cursor path — the mechanism wrinkle #2
        // in docs/guides/work-allocation.md depends on.
        var advanced = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "continue", claimed.StateVersion, null);
        advanced.Render!.StateDisplayName.Should().Be("Middle");

        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items;
        bobView.Should().NotContain(i => i.InstanceId == started.InstanceId, "the claim must still be in effect after the plain hop");

        var aliceView = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        aliceView.ClaimState.Should().Be(QueueWorkItemClaimState.ClaimedByMe);
        aliceView.StageKey.Should().Be("middle");
    }

    [Fact]
    public void Claim_SurvivesAChangeLinkJump()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var jumped = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "change:middle", claimed.StateVersion, null);
        jumped.Render!.StateDisplayName.Should().Be("Middle");

        engine.GetQueueWorkItems("bob", SharedQueueProfile).Items
            .Should().NotContain(i => i.InstanceId == started.InstanceId, "the claim must still be in effect after the change-link jump");
    }

    [Fact]
    public void Claim_ClearsAutomatically_WhenTheCursorCrossesASplitGateway()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", claimed.StateVersion, null);
        afterSplit.ResponseState.Should().Be("defer", "alice's own cursor now waits at the join");

        // A brand-new RequestCursor was minted for the join wait — the claim doesn't survive the
        // Split crossing, so bob can see it again (this is the deliberate design, not a bug).
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.ClaimState.Should().BeNull("a Waiting row has nothing to claim");
    }

    [Fact]
    public void Claim_ClearsAutomatically_AfterAJoinReleases()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var claimed = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", claimed.StateVersion, null);

        // Directly advance the automation cursor to complete the join — no support-system-call
        // needed for this test, just a plain single-cursor advance under a different profile.
        // "In review" is a bare panel — InferStepType reads that as "confirmation" (ResponseState
        // "complete") regardless of it still having an outgoing route; a real, pre-existing engine
        // nuance (see IsTerminalInstance's own doc comment), not something this test is about.
        var automationStart = engine.GetCurrent(DefinitionKey, TenantId, "system", AutomationProfile, afterSplit.InstanceId);
        automationStart.Render!.StateDisplayName.Should().Be("In review");
        // The release moves the instance's primary visible position into the CASEWORKER queue's
        // "approved" stage — AutomationProfile (VisibleQueues: ["automation"] only) genuinely has
        // nothing left to see on this instance at all once that happens, so `released` itself
        // (built with AutomationProfile) correctly reports ACCESS_DENIED; check the release landed
        // via a caseworker-side view instead.
        var released = engine.Advance(
            afterSplit.InstanceId, TenantId, "system", AutomationProfile, "approved", automationStart.StateVersion, null);
        released.ResponseState.Should().Be("error");
        released.Problems.Should().Contain(p => p.Code == "ACCESS_DENIED");

        var caseworkerView = engine.GetCurrent(DefinitionKey, TenantId, "bob", SharedQueueProfile, afterSplit.InstanceId);
        caseworkerView.Render!.StateDisplayName.Should().Be("Approved");

        // "Approved" is itself a bare panel (Done, per QueueWorkItemStatus) — request it explicitly.
        var bobView = engine.GetQueueWorkItems("bob", SharedQueueProfile, statuses: [QueueWorkItemStatus.Done]).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        bobView.ClaimState.Should().BeNull("a Done row has nothing to claim — no stale claim carried forward either way");
    }

    [Fact]
    public void NotClaimable_AWaitingRow_ReturnsNotClaimable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "go-wait", started.StateVersion, null);

        // The join gateway's own cursor id — fetch it from the queue list first.
        var waitingItem = engine.GetQueueWorkItems("alice", SharedQueueProfile).Items.Single(i => i.InstanceId == afterSplit.InstanceId);
        var realAttempt = engine.ClaimWorkItem(afterSplit.InstanceId, waitingItem.CursorId, TenantId, "alice", SharedQueueProfile);

        realAttempt.ResponseState.Should().Be("error");
        realAttempt.Problems.Should().Contain(p => p.Code == "NOT_CLAIMABLE");
    }

    [Fact]
    public void NotClaimable_ADoneRow_ReturnsNotClaimable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", SharedQueueProfile);
        var afterMiddle = engine.Advance(started.InstanceId, TenantId, "alice", SharedQueueProfile, "continue", started.StateVersion, null);
        var afterDone = engine.Advance(afterMiddle.InstanceId, TenantId, "alice", SharedQueueProfile, "finish", afterMiddle.StateVersion, null);
        afterDone.ResponseState.Should().Be("complete");

        var doneItem = engine.GetQueueWorkItems("alice", SharedQueueProfile, statuses: [QueueWorkItemStatus.Done]).Items
            .Single(i => i.InstanceId == started.InstanceId);

        var claimAttempt = engine.ClaimWorkItem(started.InstanceId, doneItem.CursorId, TenantId, "alice", SharedQueueProfile);

        claimAttempt.ResponseState.Should().Be("error");
        claimAttempt.Problems.Should().Contain(p => p.Code == "NOT_CLAIMABLE");
    }

    [Fact]
    public void NotClaimable_AnOwnerRestrictedProfile_ReturnsNotClaimable()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", OwnerRestrictedProfile);

        var claimAttempt = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", OwnerRestrictedProfile);

        claimAttempt.ResponseState.Should().Be("error");
        claimAttempt.Problems.Should().Contain(p => p.Code == "NOT_CLAIMABLE", "an owner-restricted instance already has exactly one possible actor — nothing to claim");
    }

    [Fact]
    public void ClaimWorkItem_OnAnUnknownInstance_ReturnsInstanceNotFound()
    {
        var engine = BuildEngine();

        var result = engine.ClaimWorkItem("does-not-exist", RequestCursor.PrimaryCursorId, TenantId, "alice", SharedQueueProfile);

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "INSTANCE_NOT_FOUND");
    }
}
