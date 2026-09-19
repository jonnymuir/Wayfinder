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
/// <see cref="ProcessManagerEngine.GetCurrentOrManualRestart"/> — the gated entry point a
/// citizen-facing surface must use for an untrusted <c>action: "start-new"</c> request. Unlike
/// <see cref="ProcessManagerEngine.GetCurrentOrStartFresh"/> (still exercised directly by
/// <c>GetCurrentOrStartFreshTests</c> as the ungated primitive trusted internal callers use), this
/// must refuse the request entirely unless the blueprint declares
/// <see cref="ServiceBlueprint.AllowManualRestart"/>.
/// </summary>
public class GetCurrentOrManualRestartTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Same shape as GetCurrentOrStartFreshTests' own fixture: "start" (not terminal) routes to
    // "done" (a bare panel, infers "confirmation" — terminal).
    private static string BlueprintJson(string definitionKey, bool allowManualRestart, string requestPolicy = "single") => $$"""
        {
          "definitionKey": "{{definitionKey}}",
          "displayName": "GetCurrentOrManualRestart Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "{{requestPolicy}}",
          "allowManualRestart": {{(allowManualRestart ? "true" : "false")}},
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

    private static ProcessManagerEngine BuildEngine(string definitionKey, bool allowManualRestart, string requestPolicy = "single")
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(
            BlueprintJson(definitionKey, allowManualRestart, requestPolicy), JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    private static ProcessManagerEngine AdvanceToTerminal(ProcessManagerEngine engine, string definitionKey, out ServiceRequestResponseEnvelope started)
    {
        started = engine.GetCurrent(definitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner);
        var advanced = engine.Advance(started.InstanceId, TenantId, UserId, "continue", started.StateVersion, null);
        advanced.Render!.StateDisplayName.Should().Be("Done", "sanity check: the instance really is terminal now");
        return engine;
    }

    [Fact]
    public void NotAllowed_TerminalExisting_NeverCreatesAFreshInstance()
    {
        var engine = BuildEngine("not-allowed-terminal", allowManualRestart: false);
        AdvanceToTerminal(engine, "not-allowed-terminal", out var started);

        var result = engine.GetCurrentOrManualRestart(
            "not-allowed-terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().Be(started.InstanceId, "the blueprint never opted in — the terminal instance must not be silently abandoned");
        engine.GetAllInstances().Should().ContainSingle(i => i.BlueprintKey == "not-allowed-terminal");
    }

    [Fact]
    public void Allowed_TerminalExisting_StartsAGenuinelyFreshInstance()
    {
        var engine = BuildEngine("allowed-terminal", allowManualRestart: true);
        AdvanceToTerminal(engine, "allowed-terminal", out var started);

        var result = engine.GetCurrentOrManualRestart(
            "allowed-terminal", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().NotBe(started.InstanceId);
        result.Render!.StateDisplayName.Should().Be("Start");
        engine.GetAllInstances().Should().HaveCount(2, "the terminal instance must still exist, not be replaced");
    }

    [Fact]
    public void NotAllowed_NonTerminalExisting_IsReinstatedNotAbandoned()
    {
        var engine = BuildEngine("not-allowed-in-progress", allowManualRestart: false);
        var started = engine.GetCurrent("not-allowed-in-progress", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        var result = engine.GetCurrentOrManualRestart(
            "not-allowed-in-progress", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().Be(started.InstanceId);
        engine.GetAllInstances().Should().ContainSingle();
    }

    [Fact]
    public void Allowed_NonTerminalExisting_CreatesAFreshInstance_MatchingPriorUnconditionalBehaviour()
    {
        // Once allowed, this must behave exactly like the raw, pre-existing action: "start-new"
        // handling — including abandoning a non-terminal instance. That's not a gap: it's what the
        // "prompt" policy's own instance-picker "Start a new request" choice has always relied on
        // (a citizen who was just shown "you have an existing request" and deliberately chose to
        // start over anyway) — GetCurrentOrStartFresh's stronger "never abandon non-terminal work"
        // restriction is for a different, *ambient* scenario and would silently turn that picker
        // choice into a no-op if used here instead.
        var engine = BuildEngine("allowed-in-progress", allowManualRestart: true);
        var started = engine.GetCurrent("allowed-in-progress", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        var result = engine.GetCurrentOrManualRestart(
            "allowed-in-progress", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().NotBe(started.InstanceId);
        engine.GetAllInstances().Should().HaveCount(2, "the in-progress instance must still exist, not be replaced");
    }

    [Fact]
    public void NotAllowed_NoExistingInstance_StillCreatesOneNormally()
    {
        var engine = BuildEngine("not-allowed-fresh", allowManualRestart: false);

        var result = engine.GetCurrentOrManualRestart(
            "not-allowed-fresh", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.ResponseState.Should().Be("render");
        result.Render!.StateDisplayName.Should().Be("Start");
    }

    [Fact]
    public void EnvelopeAlwaysCarriesTheBlueprintsDeclaredFlag()
    {
        var allowedEngine = BuildEngine("flag-allowed", allowManualRestart: true);
        var notAllowedEngine = BuildEngine("flag-not-allowed", allowManualRestart: false);

        allowedEngine.GetCurrent("flag-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner)
            .AllowManualRestart.Should().BeTrue();
        notAllowedEngine.GetCurrent("flag-not-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner)
            .AllowManualRestart.Should().BeFalse();
    }

    [Fact]
    public void Prompt_Allowed_InstancePickersStartNewChoice_CreatesAFreshInstanceEvenThoughExistingIsNonTerminal()
    {
        // The real-world case this whole method exists to keep working: a "prompt"-policy citizen
        // is shown the instance picker (because a non-terminal existing instance was found), reads
        // "you have an existing request", and deliberately clicks "Start a new request" anyway.
        var engine = BuildEngine("prompt-allowed", allowManualRestart: true, requestPolicy: "prompt");
        var started = engine.GetCurrent("prompt-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        started.ResponseState.Should().Be("render", "sanity check: nothing existed yet, so this just started one");

        var picker = engine.GetCurrent("prompt-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner);
        picker.ResponseState.Should().Be("instance_picker", "sanity check: the non-terminal instance now triggers the picker");

        var result = engine.GetCurrentOrManualRestart("prompt-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        result.InstanceId.Should().NotBe(started.InstanceId);
        result.ResponseState.Should().Be("render");
        engine.GetAllInstances().Should().HaveCount(2, "the original instance must still exist, not be replaced");
    }

    [Fact]
    public void Prompt_NotAllowed_InstancePickersStartNewChoice_IsIgnored()
    {
        var engine = BuildEngine("prompt-not-allowed", allowManualRestart: false, requestPolicy: "prompt");
        var started = engine.GetCurrent("prompt-not-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        var result = engine.GetCurrentOrManualRestart("prompt-not-allowed", TenantId, UserId, ActorProfile.UnrestrictedOwner);

        // Falls through to ambient GetCurrent, which for "prompt" with a non-terminal existing
        // instance means the picker again — not a new instance, and not the plain form either.
        result.ResponseState.Should().Be("instance_picker");
        engine.GetAllInstances().Should().ContainSingle();
    }
}
