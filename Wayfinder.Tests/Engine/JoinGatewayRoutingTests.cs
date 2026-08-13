using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Covers a Join gateway with more than one outgoing route: it must release only the route whose
/// trigger matches the action that produced the cursor completing the join (e.g. "approve" vs
/// "reject"), not fire every outgoing route — and a Join with a single outgoing route must keep
/// behaving exactly as before. See docs/guides/reference-service-blueprint-contract.md "Gateways
/// and routing" and the juggling-licence "post-review" gateway for the motivating shape.
/// </summary>
public class JoinGatewayRoutingTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Two queues converge on one Join gateway with two outgoing routes ("approve"/"reject"),
    // mirroring juggling-licence's under-review -> post-review shape.
    private const string BranchingJoinDefinitionKey = "branching-join-test";
    private const string BranchingJoinJson = """
        {
          "definitionKey": "branching-join-test",
          "displayName": "Branching Join Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "queues": [
            { "key": "applicant", "displayName": "Applicant", "actor": "applicant" },
            { "key": "reviewer", "displayName": "Reviewer", "actor": "reviewer" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "applicant",
              "components": [ { "type": "panel", "heading": "Start" } ],
              "routes": [
                { "id": "start--continue--fan-out", "target": "fan-out", "trigger": "continue" }
              ]
            },
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "reviewer",
              "components": [ { "type": "panel", "heading": "Review" } ],
              "routes": [
                { "id": "review--approve--join", "target": "join", "trigger": "approve" },
                { "id": "review--reject--join", "target": "join", "trigger": "reject" }
              ]
            },
            {
              "stageKey": "approved",
              "displayName": "Approved",
              "queueKey": "applicant",
              "components": [ { "type": "panel", "heading": "Approved" } ]
            },
            {
              "stageKey": "rejected",
              "displayName": "Rejected",
              "queueKey": "applicant",
              "components": [ { "type": "panel", "heading": "Rejected" } ]
            }
          ],
          "gateways": [
            {
              "key": "fan-out",
              "displayName": "Hand off to reviewer",
              "gatewayType": "Split",
              "queueKey": "applicant",
              "routes": [
                { "id": "fan-out--continue--review", "target": "review", "trigger": "continue" },
                { "id": "fan-out--continue--join", "target": "join", "trigger": "continue" }
              ]
            },
            {
              "key": "join",
              "displayName": "Under review",
              "gatewayType": "Join",
              "queueKey": "applicant",
              "routes": [
                { "id": "join--approve--approved", "target": "approved", "trigger": "approve" },
                { "id": "join--reject--rejected", "target": "rejected", "trigger": "reject" }
              ],
              "requiredIncomingQueues": ["applicant", "reviewer"]
            }
          ]
        }
        """;

    // Split -> single-route Join -> stage, with no requiredIncomingQueues. Regression guard for
    // the pre-existing "always fire the one outgoing route" behaviour.
    private const string SingleRouteJoinDefinitionKey = "single-route-join-test";
    private const string SingleRouteJoinJson = """
        {
          "definitionKey": "single-route-join-test",
          "displayName": "Single Route Join Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Citizen", "actor": "citizen" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "citizen",
              "components": [ { "type": "panel", "heading": "Start" } ],
              "routes": [
                { "id": "start--continue--split", "target": "split", "trigger": "continue" }
              ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "citizen",
              "components": [ { "type": "panel", "heading": "Done" } ]
            }
          ],
          "gateways": [
            {
              "key": "split",
              "displayName": "Split",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "split--continue--join", "target": "join", "trigger": "continue" }
              ]
            },
            {
              "key": "join",
              "displayName": "Join",
              "gatewayType": "Join",
              "queueKey": "citizen",
              "routes": [
                { "id": "join--continue--done", "target": "done", "trigger": "continue" }
              ]
            }
          ]
        }
        """;

    private static ProcessManagerEngine BuildEngine(string json)
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(json, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    [Fact]
    public void Join_ReleasesOnlyTheApproveRoute_WhenApproveArrives()
    {
        var engine = BuildEngine(BranchingJoinJson);

        var started = engine.GetCurrent(BranchingJoinDefinitionKey, TenantId, UserId);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, UserId, "continue", started.StateVersion, null);

        // The split has parked the applicant's cursor at the join (still waiting on the reviewer)
        // and put the reviewer's cursor at "review" — with an unrestricted profile both are
        // accessible, and the reviewer's own stage is what's actionable next.
        Assert.Contains("approve", afterSplit.Render?.AvailableActions.Select(a => a.ActionKey) ?? []);

        var released = engine.Advance(
            afterSplit.InstanceId, TenantId, UserId, "approve", afterSplit.StateVersion, null);

        Assert.Empty(released.Problems);
        Assert.Equal("Approved", released.Render?.StateDisplayName);
    }

    [Fact]
    public void Join_ReleasesOnlyTheRejectRoute_WhenRejectArrives()
    {
        var engine = BuildEngine(BranchingJoinJson);

        var started = engine.GetCurrent(BranchingJoinDefinitionKey, TenantId, UserId);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, UserId, "continue", started.StateVersion, null);

        var released = engine.Advance(
            afterSplit.InstanceId, TenantId, UserId, "reject", afterSplit.StateVersion, null);

        Assert.Empty(released.Problems);
        Assert.Equal("Rejected", released.Render?.StateDisplayName);
    }

    [Fact]
    public void Join_WithSingleOutgoingRoute_StillReleasesUnconditionally()
    {
        var engine = BuildEngine(SingleRouteJoinJson);

        var started = engine.GetCurrent(SingleRouteJoinDefinitionKey, TenantId, UserId);
        var result = engine.Advance(started.InstanceId, TenantId, UserId, "continue", started.StateVersion, null);

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }

    private static string JugglingLicenceSeedPath([CallerFilePath] string testFilePath = "") =>
        Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..",
            "Wayfinder.ReferenceApp", "service-blueprints", "juggling-licence.json");

    [Fact]
    public void RealJugglingLicenceBlueprint_MergedJoinGatewayPassesValidation()
    {
        var json = File.ReadAllText(JugglingLicenceSeedPath());
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(json, JsonOptions)!;

        var routingDiagnostics = definition.ValidateGatewayRouting();
        var reachabilityDiagnostics = definition.ValidateReachability();

        Assert.DoesNotContain(routingDiagnostics, d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error);
        Assert.DoesNotContain(reachabilityDiagnostics, d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error);

        // The two former "Application under review" joins are one gateway with both outcomes —
        // plus, since the support-systems feature landed, a second, genuinely different join
        // ("insurer-check-complete") merging the caseworker back with the automation queue's own
        // SafetyNet Underwriting call. Two Join gateways for two distinct reasons, not a
        // regression of the original merge this test guards.
        var joinGateways = definition.Gateways!
            .Where(g => string.Equals(g.GatewayType, "Join", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, joinGateways.Count);

        var postReview = Assert.Single(joinGateways, g => g.Key == "post-review");
        Assert.Equal(2, postReview.Routes?.Count);
        Assert.Contains(postReview.Routes!, r => r.Trigger == "approve" && r.Target == "approved");
        Assert.Contains(postReview.Routes!, r => r.Trigger == "reject" && r.Target == "rejected");

        var insurerCheckComplete = Assert.Single(joinGateways, g => g.Key == "insurer-check-complete");
        Assert.Equal(["caseworker", "automation"], insurerCheckComplete.RequiredIncomingQueues);
        // Releases into the caseworker's own decision stage — not back to the review stage they
        // came from — so the insurer's verdict is in front of them when they actually decide.
        Assert.Contains(insurerCheckComplete.Routes!, r => r.Trigger == "approved" && r.Target == "caseworker-decision");
        Assert.Contains(insurerCheckComplete.Routes!, r => r.Trigger == "rejected" && r.Target == "caseworker-decision");
    }

    [Fact]
    public void ValidateGatewayRouting_FlagsBlankTriggerOnMultiRouteJoin()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BranchingJoinJson, JsonOptions)!;
        var withBlankTrigger = definition with
        {
            Gateways = definition.Gateways!.Select(g => g.Key != "join"
                ? g
                : g with
                {
                    Routes = g.Routes!.Select(r => r.Id == "join--reject--rejected" ? r with { Trigger = "" } : r).ToList()
                }).ToList()
        };

        var diagnostics = withBlankTrigger.ValidateGatewayRouting();

        Assert.Contains(diagnostics, d => d.Code == "JOIN_ROUTE_TRIGGER_EMPTY");
    }

    [Fact]
    public void ValidateGatewayRouting_FlagsDuplicateTriggerOnMultiRouteJoin()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BranchingJoinJson, JsonOptions)!;
        var withDuplicateTrigger = definition with
        {
            Gateways = definition.Gateways!.Select(g => g.Key != "join"
                ? g
                : g with
                {
                    Routes = g.Routes!.Select(r => r.Id == "join--reject--rejected" ? r with { Trigger = "approve" } : r).ToList()
                }).ToList()
        };

        var diagnostics = withDuplicateTrigger.ValidateGatewayRouting();

        Assert.Contains(diagnostics, d => d.Code == "JOIN_ROUTE_TRIGGER_DUPLICATE");
    }
}
