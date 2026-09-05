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
/// Covers the engine actually executing a <c>support-system-call</c> action for the first time —
/// the whole point of formalising <c>ActionDefinition.Type</c> — end to end: a caseworker sends
/// something to an external support system, waits behind the same join-gateway "line of
/// visibility" screen the citizen already gets, and the external system's decision (delivered via
/// poll or webhook) releases the join into the right outgoing route with its result data merged
/// in. Mirrors the "send to insurer" shape docs/guides/support-systems.md and the approved
/// support-systems design describe, without depending on the real juggling-licence blueprint or
/// SafetyNet Underwriting (later phases).
/// </summary>
public class SupportSystemActionExecutionTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string SupportSystemKey = "safetynet-underwriting";
    private const string CapabilityKey = "validate-risk-assessment";
    private const string DefinitionKey = "support-system-test";

    // Scoped the way a real caseworker's own profile would be (see ReferenceActors.CaseworkerProfile)
    // — restricted to the caseworker queue, so the automation queue's own stage never competes as
    // a visible "primary" position the way it legitimately would under an unrestricted "god view"
    // profile. This is what actually exercises the caseworker's own wait/poll experience.
    private static readonly ActorProfile CaseworkerProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Caseworker sends a case to an external system (the automation queue) and waits on the same
    // join-gateway wait/poll machinery the citizen already gets in juggling-licence's post-review
    // — mirroring the approved design's "to-insurer-check Split -> insurer-check-complete Join"
    // shape, just with generic stage/queue names since this is testing the mechanism, not the
    // juggling scenario itself.
    private const string BlueprintJson = """
        {
          "definitionKey": "support-system-test",
          "displayName": "Support System Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" },
            { "key": "automation", "displayName": "Automation", "actor": "system" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [
                { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false }
              ],
              "routes": [
                { "id": "start--send--split", "target": "to-support-system", "trigger": "send-to-support-system" }
              ]
            },
            {
              "stageKey": "in-review",
              "displayName": "In review",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "In review" } ],
              "actions": [
                {
                  "type": "support-system-call",
                  "timing": "onEnter",
                  "params": {
                    "supportSystemKey": "safetynet-underwriting",
                    "capabilityKey": "validate-risk-assessment",
                    "inputs": { "notes": "notes" }
                  }
                }
              ],
              "routes": [
                { "id": "in-review--approved--join", "target": "check-complete", "trigger": "approved" },
                { "id": "in-review--rejected--join", "target": "check-complete", "trigger": "rejected" }
              ]
            },
            {
              "stageKey": "approved",
              "displayName": "Approved",
              "queueKey": "caseworker",
              "components": [
                { "type": "text", "fieldKey": "decisionNotes", "label": "Decision notes", "required": false }
              ]
            },
            {
              "stageKey": "rejected",
              "displayName": "Rejected",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Rejected" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-support-system",
              "displayName": "Send to support system",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [
                { "id": "to-support-system--send--join", "target": "check-complete", "trigger": "send-to-support-system" },
                { "id": "to-support-system--send--review", "target": "in-review", "trigger": "send-to-support-system" }
              ]
            },
            {
              "key": "check-complete",
              "displayName": "Check complete",
              "gatewayType": "Join",
              "queueKey": "caseworker",
              "waitingContent": "Waiting for the support system's decision.",
              "waitingPollIntervalMs": 2000,
              "routes": [
                { "id": "check-complete--approved--approved", "target": "approved", "trigger": "approved" },
                { "id": "check-complete--rejected--rejected", "target": "rejected", "trigger": "rejected" }
              ],
              "requiredIncomingQueues": ["caseworker", "automation"]
            }
          ]
        }
        """;

    private sealed class ScriptedSupportSystemClient : ISupportSystemClient
    {
        public string SupportSystemKey { get; init; } = SupportSystemActionExecutionTests.SupportSystemKey;

        public List<(string CapabilityKey, IReadOnlyDictionary<string, SupportSystemInputValue> Inputs, SupportSystemInvocationContext Context)> Invocations { get; } = [];

        public int CheckStatusCallCount { get; private set; }

        public Func<string, SupportSystemInvocationReceipt, SupportSystemOutcome?>? OnCheckStatus { get; set; }

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey,
            IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context,
            CancellationToken ct = default)
        {
            Invocations.Add((capabilityKey, inputs, context));
            return Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = "external-" + Invocations.Count });
        }

        public Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey,
            SupportSystemInvocationReceipt receipt,
            CancellationToken ct = default)
        {
            CheckStatusCallCount++;
            return Task.FromResult(OnCheckStatus?.Invoke(capabilityKey, receipt));
        }
    }

    private static SupportSystemDescriptor FixtureDescriptor(params SupportSystemCompletionMode[] modes) => new()
    {
        Key = SupportSystemKey,
        DisplayName = "SafetyNet Underwriting",
        Capabilities =
        [
            new SupportSystemCapabilityDescriptor
            {
                Key = CapabilityKey,
                DisplayName = "Validate a risk assessment",
                Inputs = [new() { Key = "notes", Title = "Notes", ValueKind = ComponentPropertyValueKind.String }],
                SupportedCompletionModes = modes,
                Outcomes =
                [
                    new() { Key = "approved", DisplayName = "Approved" },
                    new() { Key = "rejected", DisplayName = "Rejected" },
                ],
            },
        ],
    };

    private static (ProcessManagerEngine Engine, ScriptedSupportSystemClient Client) BuildEngine(
        params SupportSystemCompletionMode[] modes)
    {
        SupportSystemRegistry.ResetForTests();
        SupportSystemRegistry.Register(FixtureDescriptor(modes));

        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var client = new ScriptedSupportSystemClient();
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            supportSystemClients: [client]);

        return (engine, client);
    }

    [Fact]
    public void OnEnterAction_InvokesClientWithFieldRefResolvedInputs_WhenAutomationCursorLandsOnItsStage()
    {
        var (engine, client) = BuildEngine(SupportSystemCompletionMode.Poll);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion,
                new Dictionary<string, object?> { ["notes"] = "please check this one" });

            client.Invocations.Should().ContainSingle();
            var invocation = client.Invocations[0];
            invocation.CapabilityKey.Should().Be(CapabilityKey);
            invocation.Inputs["notes"].RawValue.Should().Be("please check this one");
            invocation.Context.InstanceId.Should().Be(started.InstanceId);
            invocation.Context.WebhookExpected.Should().BeFalse("this fixture only declares Poll support");

            // Caseworker's own cursor landed straight on the join and nothing has resolved yet —
            // same wait/poll envelope shape as juggling-licence's citizen-facing post-review join.
            afterSplit.ResponseState.Should().Be("defer");
            afterSplit.PollAfterMs.Should().Be(2000);
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Poll_ResolvesTheJoin_WhenCapabilityDeclaresPollAndTheClientReportsAnOutcome()
    {
        var (engine, client) = BuildEngine(SupportSystemCompletionMode.Poll);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);

            // Still pending: the client hasn't been told to resolve yet.
            var stillWaiting = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);
            stillWaiting.ResponseState.Should().Be("defer");
            client.CheckStatusCallCount.Should().BeGreaterThan(0);

            client.OnCheckStatus = (_, _) => new SupportSystemOutcome
            {
                OutcomeKey = "approved",
                ResultPayload = new JsonObject { ["decisionNotes"] = "Looks fine" }
            };

            var resolved = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);

            resolved.ResponseState.Should().Be("render");
            resolved.Render!.StateDisplayName.Should().Be("Approved");

            var instance = engine.GetAllInstances().Single(i => i.InstanceId == afterSplit.InstanceId);
            instance.SupportSystemInvocations.Should().ContainSingle(i => i.Resolved && i.OutcomeKey == "approved");
            instance.FieldValues["decisionNotes"].Should().Be("Looks fine");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Poll_NeverChecksStatus_WhenCapabilityOnlyDeclaresWebhookSupport()
    {
        var (engine, client) = BuildEngine(SupportSystemCompletionMode.Webhook);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);

            client.Invocations.Single().Context.WebhookExpected.Should().BeTrue();

            var polled = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);

            polled.ResponseState.Should().Be("defer");
            client.CheckStatusCallCount.Should().Be(0, "a webhook-only capability should never be polled");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void GetQueueWorkItems_ResolvesAWaitingItemViaPoll_OnADeliberateRefresh()
    {
        // The queue LIST must reflect reality on a deliberate refresh, not just a single
        // instance's own page — a caseworker staring at "Waiting" with no way to learn it's
        // actually done except by clicking in is exactly the rough edge this covers.
        var (engine, client) = BuildEngine(SupportSystemCompletionMode.Poll);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);

            var stillWaiting = engine.GetQueueWorkItems(TenantId, UserId, CaseworkerProfile).Items
                .Single(i => i.InstanceId == afterSplit.InstanceId);
            stillWaiting.StageKey.Should().Be("check-complete");
            stillWaiting.IsWaiting.Should().BeTrue();

            client.OnCheckStatus = (_, _) => new SupportSystemOutcome
            {
                OutcomeKey = "approved",
                ResultPayload = new JsonObject { ["decisionNotes"] = "Looks fine" }
            };

            // "approved" is a bare terminal stage with no outbound routes, so once resolved it
            // correctly drops off the list — same as any other completed item (see
            // bulk-data-review's own "Accept and finish" for the identical, already-established
            // behaviour). The proof the poll genuinely ran from inside GetQueueWorkItems, not a
            // side-channel GetCurrent call, is that the instance is now gone from the list at
            // all.
            var afterRefresh = engine.GetQueueWorkItems(TenantId, UserId, CaseworkerProfile).Items
                .Where(i => i.InstanceId == afterSplit.InstanceId)
                .ToList();
            afterRefresh.Should().BeEmpty();

            var instance = engine.GetAllInstances().Single(i => i.InstanceId == afterSplit.InstanceId);
            instance.SupportSystemInvocations.Should().ContainSingle(i => i.Resolved && i.OutcomeKey == "approved");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void GetQueueWorkItems_LeavesAWaitingItemAlone_WhenTheCapabilityOnlyDeclaresWebhookSupport()
    {
        // A webhook-only capability must never be polled from the list either — the same rule
        // TryPollResolveSupportSystemInvocations already enforces for a single instance's page.
        var (engine, client) = BuildEngine(SupportSystemCompletionMode.Webhook);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);

            client.CheckStatusCallCount.Should().Be(0);
            var stillWaiting = engine.GetQueueWorkItems(TenantId, UserId, CaseworkerProfile).Items
                .Single(i => i.InstanceId == afterSplit.InstanceId);

            stillWaiting.StageKey.Should().Be("check-complete");
            stillWaiting.IsWaiting.Should().BeTrue();
            client.CheckStatusCallCount.Should().Be(0, "a webhook-only capability should never be polled from the list either");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ResolveSupportSystemOutcome_ReleasesTheJoin_SimulatingAWebhookDelivery()
    {
        var (engine, _) = BuildEngine(SupportSystemCompletionMode.Webhook);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);

            var invocationId = engine.GetAllInstances()
                .Single(i => i.InstanceId == afterSplit.InstanceId)
                .SupportSystemInvocations.Single().InvocationId;

            var resolved = engine.ResolveSupportSystemOutcome(invocationId, "rejected");

            // "rejected" is a bare panel with no interactive components — a confirmation-type
            // terminal stage, the same shape juggling-licence's own approved/rejected stages use.
            resolved.ResponseState.Should().Be("complete");
            resolved.Render!.StateDisplayName.Should().Be("Rejected");

            // Persisted, not just returned transiently — a fresh GetCurrent agrees.
            var reread = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);
            reread.Render!.StateDisplayName.Should().Be("Rejected");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ResolveSupportSystemOutcome_UnknownInvocationId_ReturnsError()
    {
        var (engine, _) = BuildEngine(SupportSystemCompletionMode.Webhook);
        try
        {
            var result = engine.ResolveSupportSystemOutcome("does-not-exist", "approved");

            result.ResponseState.Should().Be("error");
            result.Problems.Should().ContainSingle(p => p.Code == "SUPPORT_SYSTEM_INVOCATION_NOT_FOUND");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ResolveSupportSystemOutcome_OutcomeNotDeclaredByCapability_ReturnsErrorAndDoesNotAdvance()
    {
        var (engine, _) = BuildEngine(SupportSystemCompletionMode.Webhook);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);
            var invocationId = engine.GetAllInstances()
                .Single(i => i.InstanceId == afterSplit.InstanceId)
                .SupportSystemInvocations.Single().InvocationId;

            var result = engine.ResolveSupportSystemOutcome(invocationId, "maybe");

            result.ResponseState.Should().Be("error");
            result.Problems.Should().ContainSingle(p => p.Code == "SUPPORT_SYSTEM_INVALID_OUTCOME");

            // Still waiting — the bad delivery must not have advanced anything.
            var stillWaiting = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);
            stillWaiting.ResponseState.Should().Be("defer");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ResolveSupportSystemOutcome_SecondDeliveryForAnAlreadyResolvedInvocation_IsANoOp()
    {
        // A capability declaring both Poll and Webhook can have both mechanisms fire for the same
        // invocation — only the first should ever advance anything.
        var (engine, _) = BuildEngine(SupportSystemCompletionMode.Poll, SupportSystemCompletionMode.Webhook);
        try
        {
            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send-to-support-system", pickedUp.StateVersion, null);
            var invocationId = engine.GetAllInstances()
                .Single(i => i.InstanceId == afterSplit.InstanceId)
                .SupportSystemInvocations.Single().InvocationId;

            var first = engine.ResolveSupportSystemOutcome(invocationId, "approved");
            first.Render!.StateDisplayName.Should().Be("Approved");

            var second = engine.ResolveSupportSystemOutcome(invocationId, "rejected");

            second.ResponseState.Should().Be("error");
            second.Problems.Should().ContainSingle(p => p.Code == "SUPPORT_SYSTEM_INVOCATION_NOT_FOUND");

            // The first (real) outcome still stands.
            var reread = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);
            reread.Render!.StateDisplayName.Should().Be("Approved");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
