using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// The wait screen at a join says what its author wrote, and names who it is still waiting for by their
/// display names, apart from that message. A queue's internal key is never shown to the person waiting.
/// </summary>
public class JoinWaitingTests
{
    private const string TenantId = "tenant";
    private const string Visitor = "visitor-1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"], StartableQueues = ["visitor"], ActionableQueues = ["visitor"], RestrictToInstanceOwner = true
    };

    // The visitor's message goes to the team, and the visitor waits at the join for the reply.
    private const string Blueprint = """
        {
          "definitionKey": "contact", "displayName": "Contact us", "version": 1, "initialStage": "message",
          "queues": [
            { "key": "visitor", "displayName": "Visitor", "actor": "citizen" },
            { "key": "team-internal-key", "displayName": "The Exchange", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "message", "displayName": "Send us a message", "queueKey": "visitor",
              "components": [ { "type": "conversation", "fieldKey": "conversation", "label": "Your message", "required": true } ],
              "routes": [ { "id": "message--send--to-team", "target": "to-team", "trigger": "send" } ]
            },
            {
              "stageKey": "reply", "displayName": "Reply", "queueKey": "team-internal-key",
              "components": [ { "type": "conversation", "fieldKey": "conversation", "label": "Your reply" } ],
              "routes": [ { "id": "reply--reply--awaiting-reply", "target": "awaiting-reply", "trigger": "reply" } ]
            },
            {
              "stageKey": "follow-up", "displayName": "Your conversation", "queueKey": "visitor",
              "components": [ { "type": "conversation", "fieldKey": "conversation", "label": "Reply to us" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-team", "displayName": "Send", "gatewayType": "Split", "queueKey": "visitor",
              "routes": [
                { "id": "to-team--send--reply", "target": "reply", "trigger": "send" },
                { "id": "to-team--send--awaiting-reply", "target": "awaiting-reply", "trigger": "send" }
              ]
            },
            {
              "key": "awaiting-reply", "displayName": "Thank you", "gatewayType": "Join", "queueKey": "visitor",
              "routes": [ { "id": "awaiting-reply--reply--follow-up", "target": "follow-up", "trigger": "reply" } ],
              "waitingContent": "We'll email you when we reply.",
              "requiredIncomingQueues": ["visitor", "team-internal-key"]
            }
          ]
        }
        """;

    private static ComponentRenderPayload WaitingAfterSending()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(Blueprint, JsonOptions)!;
        var engine = new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: new InMemoryServiceRequestStore());
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        var waiting = engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "send", started.StateVersion,
            new() { ["conversation"] = "Hello" });
        waiting.ResponseState.Should().Be("defer");
        return waiting.Render!.Components.Single(c => c.Type == "waiting");
    }

    [Fact]
    public void TheWaitScreen_SaysOnlyWhatItsAuthorWrote()
    {
        WaitingAfterSending().Content.Should().Be("We'll email you when we reply.");
    }

    [Fact]
    public void TheWaitScreen_NamesWhoItIsWaitingFor_ByDisplayName_NeverByInternalKey()
    {
        var waiting = WaitingAfterSending();

        waiting.WaitingFor.Should().Equal("The Exchange");
        JsonSerializer.Serialize(waiting).Should().NotContain("team-internal-key");
    }
}
