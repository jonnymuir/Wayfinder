using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine.Worklist;

/// <summary>
/// SECURITY REGRESSION: <see cref="Wayfinder.Engine.Worklist.WorklistExtensions.MapWorklist"/>'s
/// file-download route used to resolve its target instance via
/// <c>engine.GetAllInstances().FirstOrDefault(...)</c> — no tenant or access check at all, the
/// one route in this package that skipped the <c>CanAccessInstance</c> check every other
/// instance-scoped method applies. It now uses <see cref="IProcessManager.TryGetAccessibleInstance"/>
/// instead, which is the boundary this test exercises directly — the same "coarsest
/// fast+deterministic test" this repo's testing conventions prefer over a booted-host HTTP test
/// for behaviour that doesn't actually require one.
/// </summary>
public class WorklistFileDownloadTests
{
    private const string DefinitionKey = "file-download-test";
    private const string UserId = "user";

    private static readonly ActorProfile Caseworker = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "file-download-test",
          "displayName": "File download test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "attachment", "label": "Attachment", "required": false } ],
              "routes": [ { "id": "start--submit--done", "target": "done", "trigger": "submit" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ],
              "routes": []
            }
          ]
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static ProcessManagerEngine BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    private static string SeedInstanceWithAttachment(ProcessManagerEngine engine, string tenantId, string reference)
    {
        var started = engine.GetCurrent(DefinitionKey, tenantId, UserId, Caseworker);
        engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, tenantId, UserId, Caseworker);
        engine.Advance(started.InstanceId, tenantId, UserId, Caseworker, "submit", started.StateVersion,
            new Dictionary<string, object?> { ["attachment"] = reference });
        return started.InstanceId;
    }

    [Fact]
    public void TryGetAccessibleInstance_ReturnsNull_ForAnotherTenantsInstance()
    {
        var engine = BuildEngine();
        var instanceId = SeedInstanceWithAttachment(engine, "tenant-b", "memory://tenant-b/attachment/secret.pdf");

        var accessedAsWrongTenant = engine.TryGetAccessibleInstance(instanceId, "tenant-a", UserId, Caseworker);

        accessedAsWrongTenant.Should().BeNull(
            "a caseworker's session resolved to tenant-a must never resolve an instance belonging to tenant-b, " +
            "regardless of instanceId — this is exactly the gap GetAllInstances().FirstOrDefault(...) left open");
    }

    [Fact]
    public void TryGetAccessibleInstance_ReturnsTheInstance_ForItsOwnTenant()
    {
        var engine = BuildEngine();
        var instanceId = SeedInstanceWithAttachment(engine, "tenant-a", "memory://tenant-a/attachment/own.pdf");

        var accessed = engine.TryGetAccessibleInstance(instanceId, "tenant-a", UserId, Caseworker);

        accessed.Should().NotBeNull();
        accessed!.InstanceId.Should().Be(instanceId);
    }

    [Fact]
    public void TryGetAccessibleInstance_ReturnsNull_ForAnUnknownInstanceId()
    {
        var engine = BuildEngine();

        var accessed = engine.TryGetAccessibleInstance("does-not-exist", "tenant-a", UserId, Caseworker);

        accessed.Should().BeNull("a nonexistent instanceId must fail the same way an inaccessible one does — no distinguishing signal to a caller");
    }
}
