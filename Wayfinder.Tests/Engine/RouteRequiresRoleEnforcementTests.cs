using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="RouteFile.RequiresRole"/> — genuinely enforced against <see cref="ActorProfile.Capabilities"/>
/// as of this session's work-allocation feature. Previously declared on a route but never actually
/// checked against the accessing actor's real capabilities: <c>BuildAvailableActions</c> only ever
/// stripped a role-gated route when there was no queue context at all, regardless of whether the
/// specific actor held the role or not. See docs/guides/work-allocation.md.
/// </summary>
public class RouteRequiresRoleEnforcementTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string DefinitionKey = "requires-role-test";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "requires-role-test",
          "displayName": "Requires Role Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [
                { "id": "start--approve--done", "target": "done", "trigger": "approve", "requiresRole": "senior-caseworker" },
                { "id": "start--continue--done", "target": "done", "trigger": "continue" }
              ]
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

    private static ActorProfile ProfileWith(params string[] capabilities) => new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
        Capabilities = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void ARoleGatedRoute_IsHiddenFromAnActorWithoutTheRole()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, ProfileWith());

        started.Render!.AvailableActions.Should().NotContain(a => a.ActionKey == "approve");
        started.Render.AvailableActions.Should().Contain(a => a.ActionKey == "continue", "an ungated route on the same stage stays available");
    }

    [Fact]
    public void ARoleGatedRoute_IsAvailable_ToAnActorHoldingTheRole()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, ProfileWith("senior-caseworker"));

        started.Render!.AvailableActions.Should().Contain(a => a.ActionKey == "approve");
    }

    [Fact]
    public void AttemptingToAdvance_ViaTheGatedAction_WithoutTheRole_FailsWithInvalidTransition()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, ProfileWith());

        var result = engine.Advance(started.InstanceId, TenantId, UserId, ProfileWith(), "approve", started.StateVersion, null);

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");
    }
}
