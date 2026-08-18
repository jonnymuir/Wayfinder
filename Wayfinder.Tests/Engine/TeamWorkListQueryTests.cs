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
/// <c>ProcessManagerEngine.GetTeamWorkItems</c> — a team's own aggregate view of everything it
/// owns (see docs/guides/team-assignment.md), as opposed to <c>GetQueueWorkItems</c>'s per-user
/// "what can I personally act on right now" view. Reuses the same team-tray test blueprint shape
/// as <c>TeamAssignmentTests</c>.
/// </summary>
public class TeamWorkListQueryTests
{
    private const string TenantId = "tenant";
    private const string OtherTenantId = "other-tenant";
    private const string DefinitionKey = "team-worklist-test";

    private static readonly ActorProfile ReviewersProfile = new()
    {
        VisibleQueues = ["review-team"],
        StartableQueues = ["review-team"],
        ActionableQueues = ["review-team"],
        RestrictToInstanceOwner = false,
        TeamIds = new HashSet<string> { "reviewers" }
    };

    private static readonly ActorProfile NotAReviewerProfile = new()
    {
        VisibleQueues = ["review-team"],
        StartableQueues = ["review-team"],
        ActionableQueues = ["review-team"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "team-worklist-test",
          "displayName": "Team Worklist Test",
          "version": 1,
          "initialStage": "triage",
          "requestPolicy": "multiple",
          "queues": [
            { "key": "review-team", "displayName": "Review Team", "actor": "caseworker", "assignmentPolicy": "team-tray", "owningTeamId": "reviewers" }
          ],
          "stages": [
            {
              "stageKey": "triage",
              "displayName": "Triage",
              "queueKey": "review-team",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "triage--resolve--resolved", "target": "resolved", "trigger": "resolve" } ]
            },
            {
              "stageKey": "resolved",
              "displayName": "Resolved",
              "queueKey": "review-team",
              "components": [ { "type": "panel", "heading": "Resolved" } ]
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
    public void ShowsBothUnpickedAndTeammateHeldRows()
    {
        var engine = BuildEngine();
        var unpicked = engine.GetCurrent(DefinitionKey, TenantId, "alice", ReviewersProfile);
        var toBePicked = engine.GetCurrent(DefinitionKey, TenantId, "bob", ReviewersProfile);
        // Two distinct instances since requestPolicy is "multiple" and each userId starts its own.
        var item = engine.GetQueueWorkItems("chris", ReviewersProfile).Items.Single(i => i.InstanceId == toBePicked.InstanceId);
        engine.ClaimWorkItem(toBePicked.InstanceId, item.CursorId, TenantId, "chris", ReviewersProfile);

        var teamView = engine.GetTeamWorkItems(TenantId, "reviewers", ReviewersProfile).Items;

        teamView.Should().Contain(i => i.InstanceId == unpicked.InstanceId && i.Status == QueueWorkItemStatus.Unassigned,
            "still sitting in the tray, unpicked");
        teamView.Should().Contain(i => i.InstanceId == toBePicked.InstanceId && i.Status == QueueWorkItemStatus.Actionable,
            "picked up by chris — the team view shows it regardless of who holds it");
    }

    [Fact]
    public void HidesRowsBelongingToADifferentTeam()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", ReviewersProfile);

        engine.GetTeamWorkItems(TenantId, "some-other-team", ReviewersProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "belongs to \"reviewers\", not \"some-other-team\"");
    }

    [Fact]
    public void EmptyEnvelope_ForACallerWhoIsNotAMemberOfTheTeamTheyAskAbout()
    {
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "alice", ReviewersProfile);

        var result = engine.GetTeamWorkItems(TenantId, "reviewers", NotAReviewerProfile);

        result.Items.Should().BeEmpty("NotAReviewerProfile can view/act in the queue but isn't on the \"reviewers\" team");
    }

    [Fact]
    public void ScopedToTenant()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, OtherTenantId, "alice", ReviewersProfile);

        engine.GetTeamWorkItems(TenantId, "reviewers", ReviewersProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "a different tenant's instance must never leak across");
    }

    [Fact]
    public void NeverReturnsALegacyQueuesRow()
    {
        // "reviewers" only ever owns "review-team" in this blueprint — there's no legacy (no
        // AssignmentPolicy) queue here to accidentally leak in, but assert the team-scoping filter
        // itself rather than relying on the blueprint's own shape as the only proof: an unrelated
        // team id genuinely owning nothing returns nothing, not "everything unscoped".
        var engine = BuildEngine();
        engine.GetCurrent(DefinitionKey, TenantId, "alice", ReviewersProfile);

        var noTeamProfile = ReviewersProfile with { TeamIds = new HashSet<string> { "nobody-owns-this-team" } };
        engine.GetTeamWorkItems(TenantId, "nobody-owns-this-team", noTeamProfile).Items.Should().BeEmpty();
    }
}
