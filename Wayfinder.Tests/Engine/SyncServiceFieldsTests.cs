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
/// <c>ProcessManagerEngine.SyncServiceFields</c> — writing a field value into
/// <c>ServiceRequest.FieldValues</c> outside of a stage transition (see
/// docs/guides/bulk-data-review.md, the bulk-dataset row-correction feature this exists for). Its
/// sole authorization boundary is that every key must be declared <c>source: "service"</c> under
/// the blueprint's own <c>calculations.fields</c> — this suite is what proves that boundary is
/// real, and that a value written this way is genuinely picked up by the exact same
/// <c>showWhen</c> enforcement every other field already gets, with no separate recalculation step.
/// </summary>
public class SyncServiceFieldsTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "sync-fields-test";

    private static readonly ActorProfile Profile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    private static readonly ActorProfile OwnerRestrictedProfile = Profile with { RestrictToInstanceOwner = true };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // "notes" is an ordinary captured input — the negative case (rejecting a write to it) proves
    // the authorization boundary is genuinely enforced, not just documented. "syncedFlag" is the
    // one field declared source: "service", and gates "finish" via showWhen exactly the way
    // contributionsErrorCount/contributionsDirtyCount will in the real bulk-data-review feature.
    private const string BlueprintJson = """
        {
          "definitionKey": "sync-fields-test",
          "displayName": "Sync Fields Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "calculations": {
            "fields": {
              "syncedFlag": { "source": "service" }
            }
          },
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [
                { "id": "start--finish--done", "target": "done", "trigger": "finish", "showWhen": "syncedFlag = 1" }
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
            new PassthroughContentSanitizer(),
            // The same passthrough Wayfinder.ReferenceApp/Program.cs itself registers for every
            // source: "service" field — SyncServiceFields' whole contract depends on this being the
            // resolver shape (write to FieldValues, read straight back off FieldValues), so a real
            // resolver must be wired here rather than relying on the null default, or evaluating
            // syncedFlag in showWhen throws CalculationException before ever reaching the write path.
            serviceInputsResolver: (instance, def, _) =>
                (def.Calculations?.Fields ?? new Dictionary<string, Wayfinder.Models.ServiceDesign.Calculations.ServiceBlueprintCalculationField>())
                    .Where(field => string.Equals(field.Value.Source, "service", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(field => field.Key, field => instance.FieldValues.GetValueOrDefault(field.Key)));
    }

    [Fact]
    public void WritesADeclaredServiceField_AndItIsImmediatelyVisibleToShowWhen()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);
        started.Render!.AvailableActions.Should().NotContain(a => a.ActionKey == "finish",
            "syncedFlag hasn't been set yet, so showWhen: \"syncedFlag = 1\" must hide the route");

        var synced = engine.SyncServiceFields(
            started.InstanceId, TenantId, "alice", Profile,
            new Dictionary<string, object?> { ["syncedFlag"] = 1m });

        synced.ResponseState.Should().Be("render");
        synced.Render!.AvailableActions.Should().Contain(a => a.ActionKey == "finish",
            "no separate recalculation step exists to trigger — the very next render must already reflect the synced value");

        // Advance() itself independently re-derives this too, not just GetCurrent/BuildEnvelope —
        // proving the enforcement isn't a rendering-only nicety.
        var advanced = engine.Advance(started.InstanceId, TenantId, "alice", Profile, "finish", synced.StateVersion, null);
        advanced.ResponseState.Should().Be("complete");
    }

    [Fact]
    public void RejectsAWriteToAFieldNotDeclaredSourceService()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);

        var result = engine.SyncServiceFields(
            started.InstanceId, TenantId, "alice", Profile,
            new Dictionary<string, object?> { ["notes"] = "sneaked in" });

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "NOT_SERVICE_FIELD");

        // The rejected write must not have landed even partially.
        var stillStarted = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile, started.InstanceId);
        stillStarted.StateVersion.Should().Be(started.StateVersion);
    }

    [Fact]
    public void RejectsAWriteToAnUndeclaredField()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);

        var result = engine.SyncServiceFields(
            started.InstanceId, TenantId, "alice", Profile,
            new Dictionary<string, object?> { ["neverDeclaredAnywhere"] = 1m });

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "NOT_SERVICE_FIELD");
    }

    [Fact]
    public void MixedUpdate_AnyNonServiceKeyRejectsTheWholeCall()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);

        var result = engine.SyncServiceFields(
            started.InstanceId, TenantId, "alice", Profile,
            new Dictionary<string, object?> { ["syncedFlag"] = 1m, ["notes"] = "sneaked in" });

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "NOT_SERVICE_FIELD");

        var stillStarted = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile, started.InstanceId);
        stillStarted.Render!.AvailableActions.Should().NotContain(a => a.ActionKey == "finish",
            "the whole update must be atomic — a rejected sibling key must not let syncedFlag land either");
    }

    [Fact]
    public void OnAnUnknownInstance_ReturnsInstanceNotFound()
    {
        var engine = BuildEngine();

        var result = engine.SyncServiceFields(
            "does-not-exist", TenantId, "alice", Profile,
            new Dictionary<string, object?> { ["syncedFlag"] = 1m });

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "INSTANCE_NOT_FOUND");
    }

    [Fact]
    public void ForAnOwnerRestrictedProfile_ADifferentUserIsDenied()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", OwnerRestrictedProfile);

        var result = engine.SyncServiceFields(
            started.InstanceId, TenantId, "bob", OwnerRestrictedProfile,
            new Dictionary<string, object?> { ["syncedFlag"] = 1m });

        result.ResponseState.Should().Be("error");
        result.Problems.Should().Contain(p => p.Code == "ACCESS_DENIED");
    }
}
