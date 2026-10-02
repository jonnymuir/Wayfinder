using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A team-tray queue's holder keeps the work when it leaves the queue (Split into automation) and
/// comes back (Join). Mirrors njf-coaching-register.json's own topology: citizen submit -> Split ->
/// registrar review (team-tray) -> Split into the standards system queue -> Join -> registrar again.
/// </summary>
public class TeamTrayRoundTripTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "team-tray-round-trip";

    private static readonly ActorProfile CitizenProfile = new()
    {
        VisibleQueues = ["applicant"],
        StartableQueues = ["applicant"],
        ActionableQueues = ["applicant"],
        RestrictToInstanceOwner = true
    };

    private static readonly ActorProfile RegistrarProfile = new()
    {
        VisibleQueues = ["registrar"],
        StartableQueues = [],
        ActionableQueues = ["registrar"],
        RestrictToInstanceOwner = false,
        TeamIds = new HashSet<string> { "registrars" }
    };

    private static readonly ActorProfile StandardsProfile = new()
    {
        VisibleQueues = ["standards"],
        StartableQueues = [],
        ActionableQueues = ["standards"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "team-tray-round-trip",
          "displayName": "Team tray round trip",
          "version": 1,
          "initialStage": "apply",
          "requestPolicy": "single",
          "queues": [
            { "key": "applicant", "displayName": "Coach", "actor": "citizen" },
            { "key": "registrar", "displayName": "Registrar", "actor": "caseworker", "assignmentPolicy": "team-tray", "owningTeamId": "registrars" },
            { "key": "standards", "displayName": "Standards", "actor": "system" }
          ],
          "stages": [
            {
              "stageKey": "apply", "displayName": "Apply", "queueKey": "applicant",
              "components": [ { "type": "text", "fieldKey": "name", "label": "Name", "required": false } ],
              "routes": [ { "id": "apply--submit--to-review", "target": "to-review", "trigger": "submit" } ]
            },
            {
              "stageKey": "review", "displayName": "Review", "queueKey": "registrar",
              "components": [ { "type": "text", "fieldKey": "reviewNote", "label": "Note", "required": false } ],
              "routes": [ { "id": "review--check--to-standards", "target": "to-standards", "trigger": "check" } ]
            },
            {
              "stageKey": "standards-validation", "displayName": "Standards check", "queueKey": "standards",
              "components": [ { "type": "panel", "heading": "Checking" } ],
              "routes": [ { "id": "sv--accredited--standards-done", "target": "standards-done", "trigger": "accredited" } ]
            },
            {
              "stageKey": "registrar-decision", "displayName": "Confirm the outcome", "queueKey": "registrar",
              "components": [ { "type": "text", "fieldKey": "decisionNote", "label": "Note", "required": false } ],
              "routes": [ { "id": "rd--record--decision-made", "target": "decision-made", "trigger": "record" } ]
            },
            {
              "stageKey": "outcome", "displayName": "Outcome", "queueKey": "applicant", "stageType": "Confirmation",
              "components": [ { "type": "panel", "heading": "Done" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-review", "displayName": "To review", "gatewayType": "Split", "queueKey": "applicant",
              "routes": [
                { "id": "to-review--submit--decision-made", "target": "decision-made", "trigger": "submit" },
                { "id": "to-review--submit--review", "target": "review", "trigger": "submit" }
              ]
            },
            {
              "key": "to-standards", "displayName": "To standards", "gatewayType": "Split", "queueKey": "registrar",
              "routes": [
                { "id": "to-standards--check--standards-done", "target": "standards-done", "trigger": "check" },
                { "id": "to-standards--check--standards-validation", "target": "standards-validation", "trigger": "check" }
              ]
            },
            {
              "key": "standards-done", "displayName": "Standards done", "gatewayType": "Join", "queueKey": "registrar",
              "waitingContent": "Waiting.", "requiredIncomingQueues": ["registrar", "standards"],
              "routes": [ { "id": "standards-done--accredited--registrar-decision", "target": "registrar-decision", "trigger": "accredited" } ]
            },
            {
              "key": "decision-made", "displayName": "Decision made", "gatewayType": "Join", "queueKey": "applicant",
              "waitingContent": "Waiting.", "requiredIncomingQueues": ["applicant", "registrar"],
              "routes": [ { "id": "decision-made--record--outcome", "target": "outcome", "trigger": "record" } ]
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
    public void TheHolderKeepsATeamTrayRowAfterItReturnsFromAutomationThroughAJoin()
    {
        var engine = BuildEngine();

        var started = engine.GetCurrent(DefinitionKey, TenantId, "alex", CitizenProfile);
        engine.Advance(started.InstanceId, TenantId, "alex", CitizenProfile, "submit", started.StateVersion, null);

        var review = engine.GetQueueWorkItems(TenantId, "casey", RegistrarProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, review.CursorId, TenantId, "casey", RegistrarProfile);
        pickedUp.ResponseState.Should().Be("render");

        // Casey runs the standards check: her own cursor waits at the join while the system queue works.
        var afterCheck = engine.Advance(started.InstanceId, TenantId, "casey", RegistrarProfile, "check", pickedUp.StateVersion, null);
        afterCheck.ResponseState.Should().Be("defer");

        var standardsItem = engine.GetQueueWorkItems(TenantId, "system", StandardsProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        var standardsPickedUp = engine.PickupWorkItem(started.InstanceId, standardsItem.CursorId, TenantId, "system", StandardsProfile);
        engine.Advance(started.InstanceId, TenantId, "system", StandardsProfile, "accredited", standardsPickedUp.StateVersion, null);

        // Back in the registrar queue, still Casey's: invisible to a teammate, actionable by her.
        engine.GetQueueWorkItems(TenantId, "jordan", RegistrarProfile).Items.Should()
            .NotContain(i => i.InstanceId == started.InstanceId, "casey held this queue's work; the round trip through automation must not return it to the tray");

        var caseysView = engine.GetQueueWorkItems(TenantId, "casey", RegistrarProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
        caseysView.PickupState.Should().Be(QueueWorkItemPickupState.PickedUpByMe);
        caseysView.Status.Should().Be(QueueWorkItemStatus.Actionable);
    }

    private sealed class WebhookClient : ISupportSystemClient
    {
        public string SupportSystemKey => "tray-test-support-system";

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey,
            IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = "external-1" });

        public Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey, SupportSystemInvocationReceipt receipt, CancellationToken ct = default) =>
            Task.FromResult<SupportSystemOutcome?>(null);
    }

    [Fact]
    public void TheHolderKeepsATeamTrayRowWhenTheSupportSystemWebhookResolvesTheAutomationStep()
    {
        SupportSystemRegistry.ResetForTests();
        SupportSystemRegistry.Register(new SupportSystemDescriptor
        {
            Key = "tray-test-support-system",
            DisplayName = "Tray test support system",
            Capabilities =
            [
                new SupportSystemCapabilityDescriptor
                {
                    Key = "check",
                    DisplayName = "Check",
                    Inputs = [new() { Key = "name", Title = "Name", ValueKind = ComponentPropertyValueKind.String }],
                    SupportedCompletionModes = [SupportSystemCompletionMode.Webhook],
                    Outcomes = [new() { Key = "accredited", DisplayName = "Accredited" }],
                },
            ],
        });

        try
        {
            var json = BlueprintJson.Replace(
                "\"components\": [ { \"type\": \"panel\", \"heading\": \"Checking\" } ],",
                "\"components\": [ { \"type\": \"panel\", \"heading\": \"Checking\" } ], " +
                "\"actions\": [ { \"type\": \"support-system-call\", \"timing\": \"onEnter\", \"params\": " +
                "{ \"supportSystemKey\": \"tray-test-support-system\", \"capabilityKey\": \"check\", \"inputs\": { \"name\": \"name\" } } } ],");
            var definition = JsonSerializer.Deserialize<ServiceBlueprint>(json, JsonOptions)!;
            var store = new InMemoryServiceRequestStore();
            var engine = new ProcessManagerEngine(
                NullLogger.Instance,
                new SingleDefinitionServiceBlueprintStore(definition),
                new PassthroughContentSanitizer(),
                instanceStore: store,
                supportSystemClients: [new WebhookClient()]);

            var started = engine.GetCurrent(DefinitionKey, TenantId, "alex", CitizenProfile);
            engine.Advance(started.InstanceId, TenantId, "alex", CitizenProfile, "submit", started.StateVersion, null);

            var review = engine.GetQueueWorkItems(TenantId, "casey", RegistrarProfile).Items.Single(i => i.InstanceId == started.InstanceId);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, review.CursorId, TenantId, "casey", RegistrarProfile);
            engine.Advance(started.InstanceId, TenantId, "casey", RegistrarProfile, "check", pickedUp.StateVersion, null);

            var invocation = store.GetAll().Single(i => i.InstanceId == started.InstanceId).SupportSystemInvocations.Single();

            // Exactly what the Automate callback does: no queue pickup, no real actor, the owner's id.
            var resolved = engine.ResolveSupportSystemOutcome(invocation.InvocationId, "accredited");
            resolved.ResponseState.Should().NotBe("error", string.Join("; ", resolved.Problems.Select(p => p.Message)));

            engine.GetQueueWorkItems(TenantId, "jordan", RegistrarProfile).Items.Should()
                .NotContain(i => i.InstanceId == started.InstanceId, "casey held this queue's work; the webhook completing the automation step must not return it to the tray");

            var caseysView = engine.GetQueueWorkItems(TenantId, "casey", RegistrarProfile).Items.Should()
                .ContainSingle(i => i.InstanceId == started.InstanceId).Subject;
            caseysView.StageKey.Should().Be("registrar-decision");
            caseysView.PickupState.Should().Be(QueueWorkItemPickupState.PickedUpByMe);
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
