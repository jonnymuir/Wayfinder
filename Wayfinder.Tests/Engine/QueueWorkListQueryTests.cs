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
/// <see cref="ProcessManagerEngine.GetQueueWorkItems"/>'s status filter/sort/search/pagination
/// query surface (see docs/guides/queue-worklist-filtering.md). One shared fixture blueprint
/// produces all four classification outcomes an instance's visible work item can settle into:
/// <c>Actionable</c> (untouched, still at "start"), <c>Waiting</c> (routed into a Split/Join, the
/// caseworker's own cursor parked at the join while the automation cursor rests on a dead-end bare
/// panel), <c>Done</c> (routed to a genuine confirmation-panel terminal stage), and the
/// "orphan" case that must stay invisible under every filter combination — no available actions
/// (every outgoing route <c>showWhen</c>-gated false), not a join gateway, and not a confirmation
/// panel either, so it's neither actionable, waiting, nor genuinely terminal.
/// </summary>
public class QueueWorkListQueryTests
{
    private const string DefinitionKey = "queue-query-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

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

    // requestPolicy "multiple" so every GetCurrent call mints a genuinely fresh instance
    // regardless of sharing the same tenant/user — lets each test build exactly the mix of
    // Actionable/Waiting/Done/orphan instances it needs without juggling distinct user ids.
    private const string BlueprintJson = """
        {
          "definitionKey": "queue-query-test",
          "displayName": "Queue Query Test",
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
              "components": [
                { "type": "text", "fieldKey": "applicantName", "label": "Applicant name", "required": false },
                { "type": "boolean", "fieldKey": "unlock", "label": "Unlock", "default": "false" }
              ],
              "routes": [
                { "id": "start--go-wait--split", "target": "to-automation", "trigger": "go-wait" },
                { "id": "start--go-done--done", "target": "done", "trigger": "go-done" },
                { "id": "start--go-hidden--hidden", "target": "hidden-stage", "trigger": "go-hidden" }
              ]
            },
            {
              "stageKey": "in-review",
              "displayName": "In review",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "In review" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ]
            },
            {
              "stageKey": "hidden-stage",
              "displayName": "Hidden stage",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [
                { "id": "hidden-stage--continue--done", "target": "done", "trigger": "continue", "showWhen": "unlock" }
              ]
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
              "routes": [ { "id": "check-complete--approved--start", "target": "start", "trigger": "approved" } ],
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

    private static string StartActionable(ProcessManagerEngine engine) =>
        engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile).InstanceId;

    private static string CreateWaiting(ProcessManagerEngine engine)
    {
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "go-wait", started.StateVersion, null);
        return afterSplit.InstanceId;
    }

    private static string CreateDone(ProcessManagerEngine engine, Dictionary<string, object?>? fieldValues = null)
    {
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var afterDone = engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "go-done", started.StateVersion, fieldValues);
        return afterDone.InstanceId;
    }

    private static string CreateOrphan(ProcessManagerEngine engine)
    {
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var afterHidden = engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerProfile, "go-hidden", started.StateVersion, null);
        return afterHidden.InstanceId;
    }

    [Fact]
    public void Default_ReproducesTodaysExactBehaviour_ActionableAndWaitingOnly_NoDoneNoOrphan()
    {
        var engine = BuildEngine();
        var actionableId = StartActionable(engine);
        var waitingId = CreateWaiting(engine);
        CreateDone(engine);
        CreateOrphan(engine);

        var items = engine.GetQueueWorkItems(UserId, CaseworkerProfile).Items;

        items.Select(i => i.InstanceId).Should().BeEquivalentTo([actionableId, waitingId]);
        items.Single(i => i.InstanceId == actionableId).Status.Should().Be(QueueWorkItemStatus.Actionable);
        var waitingItem = items.Single(i => i.InstanceId == waitingId);
        waitingItem.Status.Should().Be(QueueWorkItemStatus.Waiting);
        waitingItem.IsWaiting.Should().BeTrue("IsWaiting is derived from Status, not independently set");
    }

    [Fact]
    public void StatusesDone_SurfacesThePreviouslyHardExcludedTerminalInstance()
    {
        var engine = BuildEngine();
        StartActionable(engine);
        var doneId = CreateDone(engine);

        var items = engine.GetQueueWorkItems(UserId, CaseworkerProfile, statuses: [QueueWorkItemStatus.Done]).Items;

        var doneItem = items.Should().ContainSingle().Subject;
        doneItem.InstanceId.Should().Be(doneId);
        doneItem.Status.Should().Be(QueueWorkItemStatus.Done);
        doneItem.AvailableActions.Should().BeEmpty();
    }

    [Fact]
    public void StatusesEmptyButNonNull_ReturnsNoRows_EvenWithMatchingInstances()
    {
        var engine = BuildEngine();
        StartActionable(engine);
        CreateWaiting(engine);
        CreateDone(engine);

        var items = engine.GetQueueWorkItems(UserId, CaseworkerProfile, statuses: []).Items;

        items.Should().BeEmpty("an explicit, non-null empty status set must be respected literally, unlike null (engine default)");
    }

    [Fact]
    public void AllThreeStatusesSelected_ReturnsActionableWaitingAndDoneTogether_ButNotTheOrphan()
    {
        var engine = BuildEngine();
        var actionableId = StartActionable(engine);
        var waitingId = CreateWaiting(engine);
        var doneId = CreateDone(engine);
        CreateOrphan(engine);

        var items = engine.GetQueueWorkItems(
            UserId, CaseworkerProfile,
            statuses: [QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting, QueueWorkItemStatus.Done]).Items;

        items.Select(i => i.InstanceId).Should().BeEquivalentTo([actionableId, waitingId, doneId]);
    }

    [Fact]
    public void OrphanRow_StaysInvisible_UnderEveryStatusCombinationIncludingAllThreeSelected()
    {
        // The regression guard for the exact conflation this feature had to avoid: an instance
        // with zero available actions that is neither waiting at a join nor genuinely terminal
        // (here: every outgoing route on its stage is showWhen-gated false) must never be
        // reclassified as "Done" just because the old ".Where(actions.Count > 0 || isJoin)" filter
        // would also have hidden it. It stays invisible, exactly as it always has.
        var engine = BuildEngine();
        var orphanId = CreateOrphan(engine);

        foreach (QueueWorkItemStatus[] statuses in new[]
                 {
                     new[] { QueueWorkItemStatus.Actionable },
                     new[] { QueueWorkItemStatus.Waiting },
                     new[] { QueueWorkItemStatus.Done },
                     new[] { QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting, QueueWorkItemStatus.Done },
                 })
        {
            engine.GetQueueWorkItems(UserId, CaseworkerProfile, statuses: statuses).Items
                .Should().NotContain(i => i.InstanceId == orphanId);
        }
    }

    [Fact]
    public void Sort_DefaultOrder_TiebreaksOnInstanceId_WhenBlueprintAndStageNamesTie()
    {
        var engine = BuildEngine();
        var first = StartActionable(engine);
        var second = StartActionable(engine);
        var expectedOrder = new[] { first, second }.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        var items = engine.GetQueueWorkItems(UserId, CaseworkerProfile).Items;

        items.Select(i => i.InstanceId).Should().Equal(expectedOrder);
    }

    [Fact]
    public void Sort_CreatedAtNewestAndOldestFirst_OrderByInstanceCreationTime()
    {
        var engine = BuildEngine();
        var older = StartActionable(engine);
        Thread.Sleep(15);
        var newer = StartActionable(engine);

        var newestFirst = engine.GetQueueWorkItems(UserId, CaseworkerProfile, sort: QueueWorkListSort.CreatedAtNewestFirst).Items;
        var oldestFirst = engine.GetQueueWorkItems(UserId, CaseworkerProfile, sort: QueueWorkListSort.CreatedAtOldestFirst).Items;

        newestFirst.Select(i => i.InstanceId).Should().Equal(newer, older);
        oldestFirst.Select(i => i.InstanceId).Should().Equal(older, newer);
    }

    [Fact]
    public void Sort_UpdatedAtNewestAndOldestFirst_OrderByMostRecentAdvance()
    {
        var engine = BuildEngine();
        var untouched = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var toBump = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        Thread.Sleep(15);
        // Advancing bumps UpdatedAt regardless of the resulting status — compare the two
        // instances' own natural ordering: the one touched more recently (afterSplit, now
        // Waiting) has a newer UpdatedAt than the untouched one (still Actionable at "start").
        var afterSplit = engine.Advance(toBump.InstanceId, TenantId, UserId, CaseworkerProfile, "go-wait", toBump.StateVersion, null);

        var newestFirst = engine.GetQueueWorkItems(
            UserId, CaseworkerProfile,
            statuses: [QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting],
            sort: QueueWorkListSort.UpdatedAtNewestFirst).Items;
        var oldestFirst = engine.GetQueueWorkItems(
            UserId, CaseworkerProfile,
            statuses: [QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting],
            sort: QueueWorkListSort.UpdatedAtOldestFirst).Items;

        newestFirst.Select(i => i.InstanceId).Should().Equal(afterSplit.InstanceId, untouched.InstanceId);
        oldestFirst.Select(i => i.InstanceId).Should().Equal(untouched.InstanceId, afterSplit.InstanceId);
    }

    [Fact]
    public void Search_MatchesInstanceIdBlueprintNameStageNameAndFieldValues_CaseInsensitively()
    {
        var engine = BuildEngine();
        var actionableId = StartActionable(engine);
        var doneWithName = CreateDone(engine, new Dictionary<string, object?> { ["applicantName"] = "Alice Example" });

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, searchText: actionableId[..8]).Items
            .Should().ContainSingle(i => i.InstanceId == actionableId);

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, searchText: "queue query").Items
            .Should().Contain(i => i.InstanceId == actionableId, "BlueprintDisplayName is 'Queue Query Test'");

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, searchText: "START").Items
            .Should().Contain(i => i.InstanceId == actionableId, "StateDisplayName match must be case-insensitive");

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, statuses: [QueueWorkItemStatus.Done], searchText: "alice").Items
            .Should().ContainSingle(i => i.InstanceId == doneWithName, "a FieldValues value must be searchable too");

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, statuses: [QueueWorkItemStatus.Done], searchText: "bob").Items
            .Should().BeEmpty("no match anywhere should return nothing");

        engine.GetQueueWorkItems(UserId, CaseworkerProfile, searchText: "   ").Items.Count
            .Should().Be(engine.GetQueueWorkItems(UserId, CaseworkerProfile).Items.Count, "blank search text is a no-op");
    }

    [Fact]
    public void Pagination_ReturnsDisjointPagesWithCorrectTotalCount_AndStableOrdering()
    {
        var engine = BuildEngine();
        var ids = Enumerable.Range(0, 5).Select(_ => StartActionable(engine)).ToArray();

        var page0 = engine.GetQueueWorkItems(UserId, CaseworkerProfile, pageIndex: 0, pageSize: 2);
        var page1 = engine.GetQueueWorkItems(UserId, CaseworkerProfile, pageIndex: 1, pageSize: 2);
        var page2 = engine.GetQueueWorkItems(UserId, CaseworkerProfile, pageIndex: 2, pageSize: 2);

        page0.TotalMatchingCount.Should().Be(5);
        page1.TotalMatchingCount.Should().Be(5);
        page0.Items.Should().HaveCount(2);
        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(1);

        var page0Again = engine.GetQueueWorkItems(UserId, CaseworkerProfile, pageIndex: 0, pageSize: 2);
        page0Again.Items.Select(i => i.InstanceId).Should().Equal(page0.Items.Select(i => i.InstanceId),
            "repeated calls with no state change must return identical pages — the InstanceId tiebreak makes this deterministic");

        var allIds = page0.Items.Concat(page1.Items).Concat(page2.Items).Select(i => i.InstanceId).ToArray();
        allIds.Should().BeEquivalentTo(ids, "pages must be disjoint and together cover every matching instance exactly once");
    }

    private sealed class ScriptedSupportSystemClient : ISupportSystemClient
    {
        public string SupportSystemKey { get; init; } = "refresh-test-support-system";

        public Func<string, SupportSystemInvocationReceipt, SupportSystemOutcome?>? OnCheckStatus { get; set; }

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey,
            IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = "external-1" });

        public Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey,
            SupportSystemInvocationReceipt receipt,
            CancellationToken ct = default) =>
            Task.FromResult(OnCheckStatus?.Invoke(capabilityKey, receipt));
    }

    private const string RefreshBlueprintJson = """
        {
          "definitionKey": "queue-query-refresh-test",
          "displayName": "Queue Query Refresh Test",
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
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "start--send--split", "target": "to-support-system", "trigger": "send" } ]
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
                    "supportSystemKey": "refresh-test-support-system",
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
              "components": [ { "type": "text", "fieldKey": "decisionNotes", "label": "Decision notes", "required": false } ],
              "routes": [ { "id": "approved--continue--closed", "target": "closed", "trigger": "continue" } ]
            },
            {
              "stageKey": "closed",
              "displayName": "Closed",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Closed" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-support-system",
              "displayName": "Send to support system",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [
                { "id": "to-support-system--send--join", "target": "check-complete", "trigger": "send" },
                { "id": "to-support-system--send--review", "target": "in-review", "trigger": "send" }
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

    [Fact]
    public void RefreshIfWaitingAtJoin_StillFires_WhenWaitingIsExcludedFromTheRequestedStatusFilter()
    {
        SupportSystemRegistry.ResetForTests();
        SupportSystemRegistry.Register(new SupportSystemDescriptor
        {
            Key = "refresh-test-support-system",
            DisplayName = "Refresh Test Support System",
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
            var definition = JsonSerializer.Deserialize<ServiceBlueprint>(RefreshBlueprintJson, JsonOptions)!;
            var client = new ScriptedSupportSystemClient();
            var engine = new ProcessManagerEngine(
                NullLogger.Instance,
                new SingleDefinitionServiceBlueprintStore(definition),
                new PassthroughContentSanitizer(),
                supportSystemClients: [client]);

            var started = engine.GetCurrent("queue-query-refresh-test", TenantId, UserId, CaseworkerProfile);
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, CaseworkerProfile, "send", started.StateVersion, null);

            // Excluding Waiting entirely from the requested filter must not skip the refresh step
            // — a caseworker who has already unchecked "Waiting" still needs a genuinely-resolved
            // item to promote itself into view the moment it resolves, not stay hidden.
            var beforeResolution = engine.GetQueueWorkItems(
                UserId, CaseworkerProfile, statuses: [QueueWorkItemStatus.Actionable]).Items;
            beforeResolution.Should().NotContain(i => i.InstanceId == afterSplit.InstanceId);

            client.OnCheckStatus = (_, _) => new SupportSystemOutcome { OutcomeKey = "approved", ResultPayload = new JsonObject() };

            var afterResolution = engine.GetQueueWorkItems(
                UserId, CaseworkerProfile, statuses: [QueueWorkItemStatus.Actionable]).Items;

            var resolved = afterResolution.Should().ContainSingle(i => i.InstanceId == afterSplit.InstanceId).Subject;
            resolved.StageKey.Should().Be("approved");
            resolved.Status.Should().Be(QueueWorkItemStatus.Actionable);
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
