using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="ProcessManagerEngine.GetCurrentOrStartFresh"/> — a distinct "start a new one"
/// affordance from ambient <c>GetCurrent</c>'s "continue where I left off": reinstates a
/// non-terminal existing instance exactly as today, but genuinely starts fresh once the existing
/// one has reached a terminal stage, across all three request policies.
/// </summary>
public class GetCurrentOrStartFreshTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // "start" (a plain text question — infers step type "question", not terminal) routes to
    // "done" (a bare panel — infers "confirmation", the one thing IsTerminalInstance checks for).
    private static string BlueprintJson(string requestPolicy, string definitionKey) => $$"""
        {
          "definitionKey": "{{definitionKey}}",
          "displayName": "GetCurrentOrStartFresh Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "{{requestPolicy}}",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "start--continue--done", "target": "done", "trigger": "continue" } ]
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

    private static ProcessManagerEngine BuildEngine(string requestPolicy, string definitionKey)
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson(requestPolicy, definitionKey), JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    [Fact]
    public void Single_NoExistingInstance_CreatesOne()
    {
        var engine = BuildEngine("single", "no-existing");

        var result = engine.GetCurrentOrStartFresh("no-existing", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.ResponseState.Should().Be("render");
        result.Render!.StateDisplayName.Should().Be("Start");
    }

    [Fact]
    public void Single_NonTerminalExisting_IsReinstatedNotDuplicated()
    {
        var engine = BuildEngine("single", "non-terminal");
        var started = engine.GetCurrentOrStartFresh("non-terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        var again = engine.GetCurrentOrStartFresh("non-terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        again.InstanceId.Should().Be(started.InstanceId, "in-progress work must never be silently abandoned");
        engine.GetAllInstances().Should().ContainSingle(i => i.BlueprintKey == "non-terminal");
    }

    [Fact]
    public void Single_TerminalExisting_StartsAGenuinelyFreshInstance()
    {
        var engine = BuildEngine("single", "terminal");
        var started = engine.GetCurrentOrStartFresh("terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        var advanced = engine.Advance(started.InstanceId, TenantId, UserId, "continue", started.StateVersion, null);
        advanced.Render!.StateDisplayName.Should().Be("Done", "sanity check: the instance really is terminal now");

        // Ambient GetCurrent must still show the terminal one forever (unchanged, depended-upon
        // behaviour) — GetCurrentOrStartFresh is the only one that behaves differently here.
        var ambient = engine.GetCurrent("terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        ambient.InstanceId.Should().Be(started.InstanceId);

        var fresh = engine.GetCurrentOrStartFresh("terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        fresh.InstanceId.Should().NotBe(started.InstanceId);
        fresh.Render!.StateDisplayName.Should().Be("Start");
        engine.GetAllInstances().Should().HaveCount(2, "the terminal instance must still exist, not be replaced");
    }

    [Fact]
    public void Multiple_IsAHarmlessNoOpWrapper_AlwaysFreshEitherWay()
    {
        var engine = BuildEngine("multiple", "multi");

        var first = engine.GetCurrentOrStartFresh("multi", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        var second = engine.GetCurrentOrStartFresh("multi", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        second.InstanceId.Should().NotBe(first.InstanceId, "'multiple' already always creates new, with or without this method");
    }

    // A Split into an automation queue + a Join the caseworker's own cursor waits at — the same
    // shape njf-contributions and the SupportSystemActionExecutionTests fixture both use. The
    // automation stage's only component is a bare panel (a perfectly ordinary "please wait, this
    // is processing" screen) — InferStepType() reads that as "confirmation", same as a genuine
    // terminal stage would. Found live: this alone made an in-progress, still-waiting instance
    // register as terminal, because ServiceRequest.CurrentStage (a single legacy field covering
    // every cursor) had been set from the automation cursor, not the caseworker's own.
    private const string SplitJoinBlueprintJson = """
        {
          "definitionKey": "split-join-terminal-check",
          "displayName": "Split/Join Terminal Check",
          "version": 1,
          "initialStage": "start",
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
              "routes": [ { "id": "start--send--split", "target": "to-automation", "trigger": "send" } ]
            },
            {
              "stageKey": "in-review",
              "displayName": "In review",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "In review" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-automation",
              "displayName": "Send to automation",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [
                { "id": "to-automation--send--join", "target": "check-complete", "trigger": "send" },
                { "id": "to-automation--send--review", "target": "in-review", "trigger": "send" }
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

    private static readonly ActorProfile CaseworkerOnlyProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    [Fact]
    public void Single_CaseworkerWaitingAtJoinWhileAutomationCursorSitsOnABarePanelStage_IsNotMisreadAsTerminal()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(SplitJoinBlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrentOrStartFresh("split-join-terminal-check", TenantId, UserId, CaseworkerOnlyProfile);
        var afterSplit = engine.Advance(started.InstanceId, TenantId, UserId, CaseworkerOnlyProfile, "send", started.StateVersion, null);
        afterSplit.ResponseState.Should().Be("defer", "sanity check: the caseworker's own cursor is now genuinely waiting at the join");

        var again = engine.GetCurrentOrStartFresh("split-join-terminal-check", TenantId, UserId, CaseworkerOnlyProfile);

        again.InstanceId.Should().Be(afterSplit.InstanceId, "still waiting — must be reinstated, not treated as terminal");
        again.ResponseState.Should().Be("defer");
        engine.GetAllInstances().Should().ContainSingle();
    }

    [Fact]
    public void Prompt_NonTerminalExisting_StillReturnsInstancePicker_Unaffected()
    {
        var engine = BuildEngine("prompt", "prompt-test");
        var started = engine.GetCurrentOrStartFresh("prompt-test", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        started.ResponseState.Should().Be("render", "sanity check: started at the non-terminal 'start' stage");

        var again = engine.GetCurrentOrStartFresh("prompt-test", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        // The terminal check inside GetCurrentOrStartFresh never fires for a non-terminal existing
        // instance, so this falls straight through to ambient GetCurrent's own "prompt" behaviour
        // — instance_picker, same as if GetCurrentOrStartFresh had never been called at all.
        again.ResponseState.Should().Be("instance_picker");
        again.InstanceId.Should().Be(started.InstanceId);
    }
}
