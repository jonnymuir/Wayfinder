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
/// Mandatory team-based work assignment (see docs/guides/team-assignment.md) — a queue declaring
/// <c>QueueDefinition.AssignmentPolicy</c> requires individual assignment before a row is
/// actionable, unlike a legacy queue's optional claim (see <c>WorkAllocationClaimTests</c>, which
/// this suite is a sibling of, not a replacement for). Two policies exercised here:
/// "assign-to-initiator" (whoever starts it owns it immediately, no pick-up) and "team-tray"
/// (arrives owned by the team, visible to every member, actionable only once picked up).
/// </summary>
public class TeamAssignmentTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "team-assignment-test";

    private static readonly ActorProfile OpsProfile = new()
    {
        VisibleQueues = ["ops-team"],
        StartableQueues = ["ops-team"],
        ActionableQueues = ["ops-team"],
        RestrictToInstanceOwner = false,
        TeamIds = new HashSet<string> { "ops" }
    };

    private static readonly ActorProfile AutomationProfile = new()
    {
        VisibleQueues = ["automation"],
        StartableQueues = [],
        ActionableQueues = ["automation"],
        RestrictToInstanceOwner = false
    };

    private static readonly ActorProfile ReviewersProfile = new()
    {
        VisibleQueues = ["review-team"],
        StartableQueues = [],
        ActionableQueues = ["review-team"],
        RestrictToInstanceOwner = false,
        TeamIds = new HashSet<string> { "reviewers" }
    };

    /// <summary>Can view/act in the review-team queue but isn't a member of the team that owns
    /// it — proves the team-membership gate is a genuinely separate check from queue eligibility.</summary>
    private static readonly ActorProfile ReviewQueueViewerNotOnTeamProfile = new()
    {
        VisibleQueues = ["review-team"],
        StartableQueues = [],
        ActionableQueues = ["review-team"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Mirrors njf-contributions.json's own shape: an "ops-team" queue (assign-to-initiator) whose
    // own "review" stage can both loop back through automation (resubmit) and hand off to a
    // genuinely different, team-tray-owned queue ("review-team", escalate) — the two policies and
    // the queue-boundary reset, all in one blueprint.
    private const string BlueprintJson = """
        {
          "definitionKey": "team-assignment-test",
          "displayName": "Team Assignment Test",
          "version": 1,
          "initialStage": "upload",
          "requestPolicy": "multiple",
          "queues": [
            { "key": "ops-team", "displayName": "Ops Team", "actor": "caseworker", "assignmentPolicy": "assign-to-initiator", "owningTeamId": "ops" },
            { "key": "automation", "displayName": "Automation", "actor": "system" },
            { "key": "review-team", "displayName": "Review Team", "actor": "caseworker", "assignmentPolicy": "team-tray", "owningTeamId": "reviewers" }
          ],
          "stages": [
            {
              "stageKey": "upload",
              "displayName": "Upload",
              "queueKey": "ops-team",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "upload--submit--split", "target": "to-automation", "trigger": "submit" } ]
            },
            {
              "stageKey": "processing",
              "displayName": "Processing",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "Processing" } ],
              "routes": [ { "id": "processing--processed--join", "target": "check-complete", "trigger": "processed" } ]
            },
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "ops-team",
              "components": [ { "type": "text", "fieldKey": "notes2", "label": "Notes", "required": false } ],
              "routes": [
                { "id": "review--resubmit--split", "target": "to-automation", "trigger": "resubmit" },
                { "id": "review--escalate--split", "target": "to-review-team", "trigger": "escalate" }
              ]
            },
            {
              "stageKey": "escalated",
              "displayName": "Escalated",
              "queueKey": "review-team",
              "components": [ { "type": "text", "fieldKey": "resolution", "label": "Resolution notes", "required": false } ],
              "routes": [ { "id": "escalated--resolve--resolved", "target": "resolved", "trigger": "resolve" } ]
            },
            {
              "stageKey": "resolved",
              "displayName": "Resolved",
              "queueKey": "review-team",
              "components": [ { "type": "panel", "heading": "Resolved" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-automation",
              "displayName": "Send to automation",
              "gatewayType": "Split",
              "queueKey": "ops-team",
              "routes": [
                { "id": "to-automation--submit--join", "target": "check-complete", "trigger": "submit" },
                { "id": "to-automation--submit--processing", "target": "processing", "trigger": "submit" },
                { "id": "to-automation--resubmit--join", "target": "check-complete", "trigger": "resubmit" },
                { "id": "to-automation--resubmit--processing", "target": "processing", "trigger": "resubmit" }
              ]
            },
            {
              "key": "check-complete",
              "displayName": "Check complete",
              "gatewayType": "Join",
              "queueKey": "ops-team",
              "waitingContent": "Waiting.",
              "routes": [ { "id": "check-complete--processed--review", "target": "review", "trigger": "processed" } ],
              "requiredIncomingQueues": ["ops-team", "automation"]
            },
            {
              "key": "to-review-team",
              "displayName": "Escalate to review team",
              "gatewayType": "Split",
              "queueKey": "ops-team",
              "routes": [ { "id": "to-review-team--escalate--escalated", "target": "escalated", "trigger": "escalate" } ]
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

    /// <summary>Drives priya's instance from "upload" through the automation round trip to "review",
    /// mirroring njf-contributions.json's own submit -> split -> join -> review flow.</summary>
    private static ServiceRequestResponseEnvelope ReachReview(ProcessManagerEngine engine, string instanceId, int stateVersion, string trigger)
    {
        var afterSplit = engine.Advance(instanceId, TenantId, "priya", OpsProfile, trigger, stateVersion, null);
        afterSplit.ResponseState.Should().Be("defer", "priya's own cursor now waits at the join");

        var automationStart = engine.GetCurrent(DefinitionKey, TenantId, "system", AutomationProfile, instanceId);
        automationStart.Render!.StateDisplayName.Should().Be("Processing");

        // AutomationProfile has nothing left to see once the join releases into ops-team — same
        // ACCESS_DENIED-on-the-releasing-call shape WorkAllocationClaimTests already established.
        engine.Advance(instanceId, TenantId, "system", AutomationProfile, "processed", automationStart.StateVersion, null);

        return engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile, instanceId);
    }

    [Fact]
    public void AssignToInitiator_AutoAssignsOnCreation_AndBlocksEveryoneElseOnTheSameTeam()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);

        // Zero-cursor, pre-first-gateway state — established immediately, not deferred to claiming.
        started.ResponseState.Should().Be("render");

        engine.GetQueueWorkItems("sam", OpsProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "assigned to priya the moment she started it — invisible to a teammate who didn't");

        var priyaView = engine.GetQueueWorkItems("priya", OpsProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        priyaView.Status.Should().Be(QueueWorkItemStatus.Actionable);

        var samAdvanceAttempt = engine.Advance(started.InstanceId, TenantId, "sam", OpsProfile, "submit", started.StateVersion, null);
        samAdvanceAttempt.ResponseState.Should().Be("error");
        samAdvanceAttempt.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");

        var priyaAdvance = engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "submit", started.StateVersion, null);
        priyaAdvance.ResponseState.Should().Be("defer");
    }

    [Fact]
    public void AssignToInitiator_HasNothingToClaimOrRelease()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);

        var claimAttempt = engine.ClaimWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "priya", OpsProfile);
        claimAttempt.ResponseState.Should().Be("error");
        claimAttempt.Problems.Should().Contain(p => p.Code == "NOT_CLAIMABLE");

        var priyaView = engine.GetQueueWorkItems("priya", OpsProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        priyaView.ClaimState.Should().BeNull("already owned the moment it exists — nothing to claim or release");
    }

    [Fact]
    public void AssignToInitiator_SurvivesTheResubmitRoundTripThroughAutomationAndBackToTheSameQueue()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        atReview.Render!.StateDisplayName.Should().Be("Review");

        // Resubmit — a genuine Split/Join round trip through automation and back into "ops-team",
        // the same queue key. Under legacy rules this would clear AssignedTo entirely; here it must
        // still belong to priya and only priya on the other side.
        var afterResubmit = ReachReview(engine, started.InstanceId, atReview.StateVersion, "resubmit");
        afterResubmit.Render!.StateDisplayName.Should().Be("Review");

        engine.GetQueueWorkItems("sam", OpsProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "still priya's — the round trip through automation must not have reset it");

        var samStillCannotAct = engine.Advance(
            started.InstanceId, TenantId, "sam", OpsProfile, "resubmit", afterResubmit.StateVersion, null);
        samStillCannotAct.ResponseState.Should().Be("error");
        samStillCannotAct.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");

        var priyaCanStillAct = engine.Advance(
            started.InstanceId, TenantId, "priya", OpsProfile, "resubmit", afterResubmit.StateVersion, null);
        priyaCanStillAct.ResponseState.Should().Be("defer");
    }

    [Fact]
    public void TeamTray_UnpickedRow_VisibleToTeamMembers_NotToNonMembers_ActionableToNobody()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        // OpsProfile can't view review-team at all — priya's own render of her escalate call
        // correctly comes back ACCESS_DENIED, the same "nothing left to see" shape
        // WorkAllocationClaimTests' own join-release test already established; check the landing
        // via a reviewers-team view instead.
        var escalated = engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);
        escalated.ResponseState.Should().Be("error");
        escalated.Problems.Should().Contain(p => p.Code == "ACCESS_DENIED");

        var chrisView = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        chrisView.Status.Should().Be(QueueWorkItemStatus.Unassigned, "a team-tray row nobody has picked up yet");
        chrisView.AvailableActions.Should().BeEmpty();
        chrisView.ClaimState.Should().Be(QueueWorkItemClaimState.Unclaimed);

        engine.GetQueueWorkItems("someone-else", ReviewQueueViewerNotOnTeamProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "can view/act in the queue but isn't on the owning team — a genuinely different gate from queue eligibility");
    }

    [Fact]
    public void TeamTray_PickUp_MakesItActionableToThatPersonOnly_AndHidesItFromTeammates()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);

        var item = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        var claimed = engine.ClaimWorkItem(started.InstanceId, item.CursorId, TenantId, "chris", ReviewersProfile);
        claimed.ResponseState.Should().Be("render");

        engine.GetQueueWorkItems("jordan", ReviewersProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "picked up by chris — hidden entirely from every other team member");

        var chrisView = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        chrisView.Status.Should().Be(QueueWorkItemStatus.Actionable);
        chrisView.ClaimState.Should().Be(QueueWorkItemClaimState.ClaimedByMe);
    }

    [Fact]
    public void TeamTray_SecondClaimAttempt_ByADifferentTeamMember_ReturnsAlreadyClaimed()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);

        var item = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        engine.ClaimWorkItem(started.InstanceId, item.CursorId, TenantId, "chris", ReviewersProfile);

        var jordanClaim = engine.ClaimWorkItem(started.InstanceId, item.CursorId, TenantId, "jordan", ReviewersProfile);
        jordanClaim.ResponseState.Should().Be("error");
        jordanClaim.Problems.Should().Contain(p => p.Code == "ALREADY_CLAIMED");
    }

    [Fact]
    public void TeamTray_ClaimAttempt_ByANonTeamMember_ReturnsTeamMembershipRequired()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);

        // "someone-else" can't see the row at all (proven by the previous test) — resolve the
        // cursor id via a real team member instead, the same way an attacker who'd guessed/leaked a
        // cursor id would have to, to prove even a *known* cursor id is still rejected.
        var item = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Single(i => i.InstanceId == started.InstanceId);

        var claimAttempt = engine.ClaimWorkItem(started.InstanceId, item.CursorId, TenantId, "someone-else", ReviewQueueViewerNotOnTeamProfile);
        claimAttempt.ResponseState.Should().Be("error");
        claimAttempt.Problems.Should().Contain(p => p.Code == "TEAM_MEMBERSHIP_REQUIRED");
    }

    [Fact]
    public void TeamTray_Release_ReturnsItToTheTray_VisibleToTeammatesAgainAsUnassigned()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");
        engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);

        var item = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        engine.ClaimWorkItem(started.InstanceId, item.CursorId, TenantId, "chris", ReviewersProfile);

        var released = engine.ReleaseWorkItem(started.InstanceId, item.CursorId, TenantId, "chris", ReviewersProfile);
        released.ResponseState.Should().Be("render");

        var jordanView = engine.GetQueueWorkItems("jordan", ReviewersProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        jordanView.Status.Should().Be(QueueWorkItemStatus.Unassigned);
        jordanView.ClaimState.Should().Be(QueueWorkItemClaimState.Unclaimed);
    }

    [Fact]
    public void CrossingIntoAGenuinelyDifferentTeamOwnedQueue_StartsFreshUnderThatQueuesOwnPolicy()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");

        // priya (assign-to-initiator owner on ops-team) escalates into review-team — a genuinely
        // different team-owned queue. She has no standing there at all; the row must NOT inherit
        // her ownership, and must land in the team-tray's own default (unassigned) state.
        engine.Advance(started.InstanceId, TenantId, "priya", OpsProfile, "escalate", atReview.StateVersion, null);

        var chrisView = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        chrisView.Status.Should().Be(QueueWorkItemStatus.Unassigned, "the queue boundary resets assignment to the new queue's own policy, not priya's prior ownership");
    }

    [Fact]
    public void SystemActorUnrestrictedOwner_CanSeeATeamTrayRowItJustCausedToMaterialize()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "priya", OpsProfile);
        var atReview = ReachReview(engine, started.InstanceId, started.StateVersion, "submit");

        // The exact shape ResolveSupportSystemOutcome's own webhook-resolution recursion uses: a
        // real userId (the instance's owning user), but the synthetic ActorProfile.UnrestrictedOwner
        // rather than a real resolved profile — see FindAccessibleWorkItems' own IsVisibleToActor.
        // Without the bypass, this would incorrectly hide the very row this call just caused to
        // materialize, since UnrestrictedOwner carries no real team membership.
        var escalated = engine.Advance(
            started.InstanceId, TenantId, "priya", ActorProfile.UnrestrictedOwner, "escalate", atReview.StateVersion, null);

        escalated.ResponseState.Should().Be("render");
        escalated.Render!.StateDisplayName.Should().Be("Escalated");
    }
}
