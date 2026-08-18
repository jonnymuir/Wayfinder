using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="IAuditLogStore"/>'s wiring into <see cref="ProcessManagerEngine"/> — every real
/// transition (plain advance, "change:" jump, split fan-out, join arrival, join release) emits a
/// <see cref="AuditEventType.Transition"/> event, and a poll/webhook-resolved support-system
/// outcome is attributed to the instance's own user (it recurses through the same <c>Advance</c>
/// call every other transition goes through — there is no separate "system" plumbing; see
/// docs/guides/work-allocation.md).
/// </summary>
public class AuditLogTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    // Restricted to "caseworker" only — not "automation" too — so this actor's own visible item at
    // a join gateway resolves as the primary one (the actor-relative FindAccessibleWorkItems
    // resolution), which is what makes the join's own poll-check path fire at all. Matches the
    // established CaseworkerOnlyProfile pattern from GetCurrentOrStartFreshTests.
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

    private const string BlueprintJson = """
        {
          "definitionKey": "audit-log-test",
          "displayName": "Audit Log Test",
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
              "components": [ { "type": "panel", "heading": "Middle" } ]
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
                    "supportSystemKey": "audit-log-test-support-system",
                    "capabilityKey": "check",
                    "inputs": { "notes": "notes" }
                  }
                }
              ],
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

    private static (ProcessManagerEngine Engine, InMemoryAuditLogStore AuditLog) BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var auditLog = new InMemoryAuditLogStore();
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            auditLogStore: auditLog);
        return (engine, auditLog);
    }

    [Fact]
    public void PlainAdvance_EmitsATransitionEventAttributedToTheActingUser()
    {
        var (engine, auditLog) = BuildEngine();
        var started = engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile);

        engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "continue", started.StateVersion, null);

        var events = auditLog.GetByInstance(started.InstanceId);
        var transition = events.Should().ContainSingle(e => e.EventType == AuditEventType.Transition).Subject;
        transition.Actor.Should().Be(UserId);
        transition.FromStageKey.Should().Be("start");
        transition.ToStageKey.Should().Be("middle");
        transition.Action.Should().Be("continue");
        transition.Severity.Should().Be(AuditEventSeverity.Info);
    }

    [Fact]
    public void ChangeLinkJump_EmitsATransitionEvent()
    {
        var (engine, auditLog) = BuildEngine();
        var started = engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile);

        engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "change:middle", started.StateVersion, null);

        var events = auditLog.GetByInstance(started.InstanceId);
        var transition = events.Should().ContainSingle(e => e.EventType == AuditEventType.Transition).Subject;
        transition.Actor.Should().Be(UserId);
        transition.ToStageKey.Should().Be("middle");
        transition.Action.Should().Be("change:middle");
        transition.Detail.Should().Contain("change-link");
    }

    [Fact]
    public void SplitFanOut_EmitsATransitionEvent()
    {
        var (engine, auditLog) = BuildEngine();
        var started = engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile);

        engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "go-wait", started.StateVersion, null);

        var events = auditLog.GetByInstance(started.InstanceId);
        var splitEvent = events.Should().ContainSingle(e => e.EventType == AuditEventType.Transition).Subject;
        splitEvent.Actor.Should().Be(UserId);
        splitEvent.Action.Should().Be("go-wait");
        splitEvent.Detail.Should().Contain("fanned out");
    }

    private sealed class ScriptedSupportSystemClient : ISupportSystemClient
    {
        public string SupportSystemKey { get; init; } = "audit-log-test-support-system";
        public Func<string, SupportSystemInvocationReceipt, SupportSystemOutcome?>? OnCheckStatus { get; set; }

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey, IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context, CancellationToken ct = default) =>
            Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = "external-1" });

        public Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey, SupportSystemInvocationReceipt receipt, CancellationToken ct = default) =>
            Task.FromResult(OnCheckStatus?.Invoke(capabilityKey, receipt));
    }

    [Fact]
    public void JoinArrivalAndRelease_EmitTransitionEvents_AndPollResolvedOutcomeIsAttributedToTheInstanceOwner()
    {
        SupportSystemRegistry.ResetForTests();
        SupportSystemRegistry.Register(new SupportSystemDescriptor
        {
            Key = "audit-log-test-support-system",
            DisplayName = "Audit Log Test Support System",
            Capabilities =
            [
                new SupportSystemCapabilityDescriptor
                {
                    Key = "check",
                    DisplayName = "Check",
                    Inputs = [new() { Key = "notes", Title = "Notes", ValueKind = ComponentPropertyValueKind.String }],
                    SupportedCompletionModes = [SupportSystemCompletionMode.Poll],
                    Outcomes = [new() { Key = "approved", DisplayName = "Approved" }],
                },
            ],
        });

        try
        {
            var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
            var auditLog = new InMemoryAuditLogStore();
            var client = new ScriptedSupportSystemClient();
            var engine = new ProcessManagerEngine(
                NullLogger.Instance,
                new SingleDefinitionServiceBlueprintStore(definition),
                new PassthroughContentSanitizer(),
                supportSystemClients: [client],
                auditLogStore: auditLog);

            var started = engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "go-wait", started.StateVersion, null);

            // The caseworker's own cursor arrives at the join as part of the split's own fan-out
            // (TryReleaseJoinIfReady sees automation hasn't arrived yet, so it returns null without
            // saving) — the split's "fanned out" event is the only one recorded so far.
            var beforeResolution = auditLog.GetByInstance(afterSplit.InstanceId);
            beforeResolution.Should().ContainSingle(e => e.EventType == AuditEventType.Transition);

            client.OnCheckStatus = (_, _) => new SupportSystemOutcome { OutcomeKey = "approved", ResultPayload = new JsonObject() };

            // Poll resolution happens via GetCurrent's own deliberate-refresh path (a join-gateway
            // visible item polls any pending support-system invocation before rendering) —
            // recurses through Advance exactly as a human clicking "approve" would, attributed to
            // the instance's own UserId, not a "system" sentinel.
            engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile, afterSplit.InstanceId);

            var events = auditLog.GetByInstance(afterSplit.InstanceId);
            var releaseEvent = events.Should().Contain(e => e.EventType == AuditEventType.Transition && e.Detail == "join released").Subject;
            releaseEvent.Actor.Should().Be(UserId, "the recursive Advance call inside ResolveSupportSystemOutcome is attributed to the instance's own owner, not a literal 'system'");
            releaseEvent.ToStageKey.Should().Be("approved");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Query_FiltersByActorSeverityAndTimeRange()
    {
        var (engine, auditLog) = BuildEngine();
        var started1 = engine.GetCurrent("audit-log-test", TenantId, "alice", CaseworkerProfile);
        engine.Advance(started1.InstanceId, TenantId, "alice", CaseworkerProfile, "continue", started1.StateVersion, null);
        var started2 = engine.GetCurrent("audit-log-test", TenantId, "bob", CaseworkerProfile);
        engine.Advance(started2.InstanceId, TenantId, "bob", CaseworkerProfile, "continue", started2.StateVersion, null);

        auditLog.Query(actor: "alice").Should().OnlyContain(e => e.Actor == "alice");
        auditLog.Query(instanceId: started2.InstanceId).Should().OnlyContain(e => e.InstanceId == started2.InstanceId);
        auditLog.Query(minimumSeverity: AuditEventSeverity.Error).Should().BeEmpty("every event recorded here is Info severity");
        auditLog.Query(fromUtc: DateTimeOffset.UtcNow.AddMinutes(1)).Should().BeEmpty("nothing occurs in the future");
    }

    [Fact]
    public void ResetAndResetAll_LeaveTheAuditLogUntouched()
    {
        var (engine, auditLog) = BuildEngine();
        var started = engine.GetCurrent("audit-log-test", TenantId, UserId, CaseworkerProfile);
        engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "continue", started.StateVersion, null);

        auditLog.GetByInstance(started.InstanceId).Should().NotBeEmpty();

        engine.ResetAll();

        auditLog.GetByInstance(started.InstanceId).Should().NotBeEmpty(
            "an audit trail must outlive the instance it describes — Reset/ResetAll only clear the instance store");
        engine.GetAllInstances().Should().BeEmpty();
    }
}
