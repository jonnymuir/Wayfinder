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
/// <see cref="QueueDefinition.RoleGates"/>, now genuinely enforced against
/// <see cref="ActorProfile.Capabilities"/> via <c>ProcessManagerEngine.HasQueueEligibility</c> —
/// unlike the pre-existing, never-actually-checked <c>ServiceBlueprintRouteDefinition.RequiresRole</c>
/// (see docs/guides/work-allocation.md). This is queue *eligibility*, a distinct concept from
/// per-item pickup/ownership — see <c>QueueEligibilityTests</c>'s own name vs. a future pickup test.
/// </summary>
public class QueueEligibilityTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Two caseworker-ish queues sharing the same actor type, one gated to a single capability, one
    // gated to two (any-of), one left ungated entirely (regression: existing blueprints with no
    // RoleGates at all must stay fully unrestricted).
    private const string BlueprintJson = """
        {
          "definitionKey": "queue-eligibility-test",
          "displayName": "Queue Eligibility Test",
          "version": 1,
          "initialStage": "gated-start",
          "requestPolicy": "multiple",
          "queues": [
            { "key": "gated-single", "displayName": "Gated (single capability)", "actor": "caseworker", "roleGates": ["njf-review"] },
            { "key": "gated-any-of", "displayName": "Gated (any of two)", "actor": "caseworker", "roleGates": ["team-a", "team-b"] },
            { "key": "ungated", "displayName": "Ungated", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "gated-start",
              "displayName": "Gated start",
              "queueKey": "gated-single",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "gated-start--continue--any-of", "target": "any-of-stage", "trigger": "continue" } ]
            },
            {
              "stageKey": "any-of-stage",
              "displayName": "Any-of stage",
              "queueKey": "gated-any-of",
              "components": [ { "type": "text", "fieldKey": "notes2", "label": "Notes", "required": false } ],
              "routes": [ { "id": "any-of--continue--ungated", "target": "ungated-stage", "trigger": "continue" } ]
            },
            {
              "stageKey": "ungated-stage",
              "displayName": "Ungated stage",
              "queueKey": "ungated",
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

    private static ActorProfile ProfileWith(params string[] capabilities) => new()
    {
        VisibleQueues = ["gated-single", "gated-any-of", "ungated"],
        StartableQueues = ["gated-single"],
        ActionableQueues = ["gated-single", "gated-any-of", "ungated"],
        RestrictToInstanceOwner = false,
        Capabilities = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void EligibleActor_CanStartAndAct_InASingleCapabilityGatedQueue()
    {
        var engine = BuildEngine();
        var eligible = ProfileWith("njf-review");

        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, eligible);

        started.ResponseState.Should().Be("render");
        started.Render!.StateDisplayName.Should().Be("Gated start");
    }

    [Fact]
    public void IneligibleActor_CannotStart_InASingleCapabilityGatedQueue()
    {
        var engine = BuildEngine();
        var ineligible = ProfileWith("some-other-capability");

        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, ineligible);

        started.ResponseState.Should().Be("error", "starting the initial stage requires the 'njf-review' capability this profile doesn't hold");
    }

    [Fact]
    public void IneligibleActor_CannotSeeAGatedQueuesItem_ViaGetCurrent()
    {
        var engine = BuildEngine();
        var eligible = ProfileWith("njf-review");
        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, eligible);

        // A DIFFERENT profile, ineligible for "gated-single", tries to look at the same instance
        // directly by id — must not see it, exactly as an ordinary VisibleQueues mismatch already
        // wouldn't, since HasQueueEligibility is wired into the same CanViewQueue choke point.
        var ineligible = ProfileWith();
        var result = engine.GetCurrent("queue-eligibility-test", TenantId, "someone-else", ineligible, started.InstanceId);

        result.ResponseState.Should().Be("error");
    }

    [Fact]
    public void AnyOfCapabilities_EitherOneAloneIsSufficient()
    {
        var engine = BuildEngine();
        var startedByTeamA = engine.GetCurrent("queue-eligibility-test", TenantId, "userA", ProfileWith("njf-review", "team-a"));
        var pickedUpByTeamA = engine.PickupWorkItem(startedByTeamA.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "userA", ProfileWith("njf-review", "team-a"));
        engine.Advance(startedByTeamA.InstanceId, TenantId, "userA", ProfileWith("njf-review", "team-a"), "continue", pickedUpByTeamA.StateVersion, null);

        var teamAView = engine.GetCurrent("queue-eligibility-test", TenantId, "userA", ProfileWith("team-a"), startedByTeamA.InstanceId);
        teamAView.ResponseState.Should().Be("render", "team-a alone satisfies the any-of ['team-a','team-b'] gate");

        var teamBView = engine.GetCurrent("queue-eligibility-test", TenantId, "userA", ProfileWith("team-b"), startedByTeamA.InstanceId);
        teamBView.ResponseState.Should().Be("render", "team-b alone also satisfies the same any-of gate");

        var neitherView = engine.GetCurrent("queue-eligibility-test", TenantId, "userA", ProfileWith("njf-review"), startedByTeamA.InstanceId);
        neitherView.ResponseState.Should().Be("error", "njf-review alone satisfies neither 'team-a' nor 'team-b'");
    }

    [Fact]
    public void UngatedQueue_StaysFullyUnrestricted_RegardlessOfCapabilities()
    {
        var engine = BuildEngine();
        var noCapabilitiesAtAll = ProfileWith();
        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, ProfileWith("njf-review", "team-a"));
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, ProfileWith("njf-review", "team-a"));
        engine.Advance(started.InstanceId, TenantId, UserId, ProfileWith("njf-review", "team-a"), "continue", pickedUp.StateVersion, null);
        var afterAnyOf = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, ProfileWith("team-b"), started.InstanceId);
        engine.Advance(afterAnyOf.InstanceId, TenantId, UserId, ProfileWith("team-b"), "continue", afterAnyOf.StateVersion, null);

        var view = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, noCapabilitiesAtAll, started.InstanceId);

        view.ResponseState.Should().Be("complete", "the ungated queue declares no RoleGates, so it stays unrestricted regardless of capabilities — reaching its terminal panel stage");
        view.Render!.StateDisplayName.Should().Be("Ungated stage");
    }

    [Fact]
    public void UnrestrictedOwner_BypassesCapabilityGating_EvenWithZeroCapabilities()
    {
        // ActorProfile.UnrestrictedOwner (and every GetCurrent/Advance overload that defaults to
        // it) must keep working unchanged once a real blueprint's queue declares RoleGates — see
        // ActorProfile.HasCapability's own remarks on why an all-empty-allow-list profile bypasses
        // capability gating too.
        var engine = BuildEngine();

        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId);

        started.ResponseState.Should().Be("render");
        started.Render!.StateDisplayName.Should().Be("Gated start");
    }

    [Fact]
    public void GetQueueWorkItems_ExcludesAGatedQueuesInstance_FromAnIneligibleActor()
    {
        var engine = BuildEngine();
        var eligible = ProfileWith("njf-review");
        var started = engine.GetCurrent("queue-eligibility-test", TenantId, UserId, eligible);

        var eligibleView = engine.GetQueueWorkItems(TenantId, UserId, eligible);
        eligibleView.Items.Should().Contain(i => i.InstanceId == started.InstanceId);

        var ineligible = ProfileWith("unrelated-capability");
        var ineligibleView = engine.GetQueueWorkItems(TenantId, UserId, ineligible);
        ineligibleView.Items.Should().NotContain(i => i.InstanceId == started.InstanceId);
    }
}
