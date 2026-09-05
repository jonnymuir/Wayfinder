using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <c>ProcessManagerEngine.SyncBulkDatasetSyncState</c> — the bulk-dataset-specific glue over
/// <see cref="ProcessManagerEngine.SyncServiceFields"/> (see docs/guides/bulk-data-review.md's
/// sync-state section). Proves the full round trip a real blueprint like njf-contributions.json
/// relies on: a fresh ingest resets <c>dirtyCountField</c> to 0, a correction made afterwards is
/// invisible until this method is called, and a route <c>showWhen</c>-gated on it reacts
/// immediately once it is — with no separate recalculation step, matching
/// <see cref="SyncServiceFieldsTests"/>'s own finding that nothing needs one. Deliberately a
/// simpler blueprint than <see cref="BulkDatasetActionExecutionTests"/>'s own full support-system
/// loop — no automation queue, no scripted client — since the automation round trip itself is
/// already covered there and isn't what this suite is about.
/// </summary>
public class SyncBulkDatasetSyncStateTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "sync-bulk-dataset-test";

    private static readonly ActorProfile Profile = new()
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
          "definitionKey": "sync-bulk-dataset-test",
          "displayName": "Sync Bulk Dataset Test",
          "version": 1,
          "initialStage": "upload",
          "requestPolicy": "single",
          "calculations": {
            "fields": {
              "contributionsDirtyCount": { "source": "service" }
            }
          },
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "upload",
              "displayName": "Upload",
              "queueKey": "caseworker",
              "components": [ { "type": "file-upload", "fieldKey": "contributionsFile", "label": "File", "required": true } ],
              "routes": [ { "id": "upload--submit--split", "target": "to-review", "trigger": "submit" } ]
            },
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "reviewNotes", "label": "Notes", "required": false } ],
              "actions": [
                {
                  "type": "bulk-dataset-ingest",
                  "timing": "onEnter",
                  "params": {
                    "sourceFileField": "contributionsFile",
                    "datasetIdField": "contributionsDatasetId",
                    "dirtyCountField": "contributionsDirtyCount",
                    "columns": [
                      { "key": "memberRef", "title": "Ref", "valueKind": "String", "role": "RowKey" },
                      { "key": "memberName", "title": "Name", "valueKind": "String", "role": "Data", "editable": true }
                    ]
                  }
                }
              ],
              "routes": [ { "id": "review--accept--split", "target": "to-done", "trigger": "accept", "showWhen": "contributionsDirtyCount = 0" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-review",
              "displayName": "To review",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-review--submit--review", "target": "review", "trigger": "submit" } ]
            },
            {
              "key": "to-done",
              "displayName": "To done",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-done--accept--done", "target": "done", "trigger": "accept" } ]
            }
          ]
        }
        """;

    private static (ProcessManagerEngine Engine, InMemoryServiceRequestFileStorage FileStorage, InMemoryBulkDatasetStore BulkDatasetStore) BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var fileStorage = new InMemoryServiceRequestFileStorage();
        var bulkDatasetStore = new InMemoryBulkDatasetStore(fileStorage);
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            // Same passthrough Program.cs registers for every source: "service" field — required
            // for showWhen to evaluate contributionsDirtyCount at all (see SyncServiceFieldsTests'
            // own remarks on why this exception surfaces without it).
            serviceInputsResolver: (instance, def, _) =>
                (def.Calculations?.Fields ?? new Dictionary<string, ServiceBlueprintCalculationField>())
                    .Where(field => string.Equals(field.Value.Source, "service", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(field => field.Key, field => instance.FieldValues.GetValueOrDefault(field.Key)),
            bulkDatasetStore: bulkDatasetStore);
        return (engine, fileStorage, bulkDatasetStore);
    }

    private static async Task<ServiceRequestFileReference> SaveCsvAsync(InMemoryServiceRequestFileStorage fileStorage, string instanceId, string csv)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var storageKey = await fileStorage.SaveAsync(instanceId, "contributionsFile", stream, "contributions.csv");
        return new ServiceRequestFileReference
        {
            StorageKey = storageKey,
            OriginalFileName = "contributions.csv",
            ContentType = "text/csv",
            SizeBytes = csv.Length,
        };
    }

    /// <summary>Drives a fresh instance from "upload" to "review", where the ingest fires.</summary>
    private static async Task<(ServiceRequestResponseEnvelope AtReview, string DatasetId)> ReachReviewAsync(
        ProcessManagerEngine engine, InMemoryServiceRequestFileStorage fileStorage)
    {
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, "alice", Profile);
        var file = await SaveCsvAsync(fileStorage, started.InstanceId, string.Join('\n', "memberRef,memberName", "NJF-001,Alice"));

        var atReview = engine.Advance(
            started.InstanceId, TenantId, "alice", Profile, "submit", pickedUp.StateVersion,
            new Dictionary<string, object?> { ["contributionsFile"] = file });

        atReview.Render!.StateDisplayName.Should().Be("Review");

        // A Split gateway always fans out to fresh, unpicked cursors (see ProcessManagerEngine's
        // HandleSplitGatewayAdvance) — crossing into "review" doesn't carry alice's pickup of
        // "upload" forward, so it must be picked up again before it's actionable.
        var reviewItem = engine.GetQueueWorkItems(TenantId, "alice", Profile).Items.Single(i => i.InstanceId == atReview.InstanceId);
        atReview = engine.PickupWorkItem(atReview.InstanceId, reviewItem.CursorId, TenantId, "alice", Profile);

        var instance = engine.GetAllInstances().Single(i => i.InstanceId == started.InstanceId);
        var datasetId = instance.FieldValues["contributionsDatasetId"].Should().BeOfType<string>().Subject;
        return (atReview, datasetId);
    }

    [Fact]
    public async Task FreshIngest_ResetsDirtyCountFieldToZero()
    {
        var (engine, fileStorage, _) = BuildEngine();

        var (atReview, _) = await ReachReviewAsync(engine, fileStorage);

        var instance = engine.GetAllInstances().Single(i => i.InstanceId == atReview.InstanceId);
        instance.FieldValues["contributionsDirtyCount"].Should().Be(0m);
        atReview.Render!.AvailableActions.Should().Contain(a => a.ActionKey == "accept",
            "showWhen: \"contributionsDirtyCount = 0\" must be true immediately after a fresh ingest");
    }

    [Fact]
    public async Task ACorrection_IsInvisibleUntilSyncBulkDatasetSyncStateIsCalled_ThenHidesTheGatedRoute()
    {
        var (engine, fileStorage, bulkDatasetStore) = BuildEngine();
        var (atReview, datasetId) = await ReachReviewAsync(engine, fileStorage);

        await bulkDatasetStore.ApplyCorrectionAsync(
            atReview.InstanceId, datasetId, "NJF-001",
            new Dictionary<string, string?> { ["memberName"] = "Alice Corrected" }, "alice");

        // Not yet visible — nothing has touched FieldValues yet, exactly the real bug this feature
        // fixes: a correction alone changes nothing the engine can see.
        var stillStale = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile, atReview.InstanceId);
        stillStale.Render!.AvailableActions.Should().Contain(a => a.ActionKey == "accept",
            "a correction that hasn't been synced yet must not retroactively change route visibility on its own");

        var synced = engine.SyncBulkDatasetSyncState(atReview.InstanceId, TenantId, "alice", Profile, datasetId);

        synced.ResponseState.Should().Be("render");
        synced.Render!.AvailableActions.Should().NotContain(a => a.ActionKey == "accept",
            "the very next render must already reflect the synced dirty count — no separate recalculation step exists");

        var instance = engine.GetAllInstances().Single(i => i.InstanceId == atReview.InstanceId);
        instance.FieldValues["contributionsDirtyCount"].Should().Be(1m);

        // The server-side failsafe: Advance() itself independently re-derives this, not just the
        // rendered button — a direct POST of the trigger, bypassing whatever the UI shows, must
        // still be rejected while dirty.
        var directAttempt = engine.Advance(
            atReview.InstanceId, TenantId, "alice", Profile, "accept", synced.StateVersion, null);
        directAttempt.ResponseState.Should().Be("error");
        directAttempt.Problems.Should().Contain(p => p.Code == "INVALID_TRANSITION");
    }

    [Fact]
    public async Task RevertingTheCorrection_ThenSyncing_MakesTheGatedRouteReachableAgain()
    {
        var (engine, fileStorage, bulkDatasetStore) = BuildEngine();
        var (atReview, datasetId) = await ReachReviewAsync(engine, fileStorage);
        await bulkDatasetStore.ApplyCorrectionAsync(
            atReview.InstanceId, datasetId, "NJF-001",
            new Dictionary<string, string?> { ["memberName"] = "Alice Corrected" }, "alice");
        var dirty = engine.SyncBulkDatasetSyncState(atReview.InstanceId, TenantId, "alice", Profile, datasetId);
        dirty.Render!.AvailableActions.Should().NotContain(a => a.ActionKey == "accept");

        await bulkDatasetStore.RevertCorrectionsAsync(atReview.InstanceId, datasetId, "alice");
        var resynced = engine.SyncBulkDatasetSyncState(atReview.InstanceId, TenantId, "alice", Profile, datasetId);

        resynced.ResponseState.Should().Be("render");
        resynced.Render!.AvailableActions.Should().Contain(a => a.ActionKey == "accept");

        var advanced = engine.Advance(atReview.InstanceId, TenantId, "alice", Profile, "accept", resynced.StateVersion, null);
        advanced.ResponseState.Should().Be("complete", "\"done\" is a bare panel stage — a genuinely finished instance, not a join wait");
    }

    [Fact]
    public void ForADatasetIdThatMatchesNoDeclaringIngestAction_SyncIsANoOp_NotAnError()
    {
        // No ingest has run yet on this instance (still on "upload"), so nothing in FieldValues
        // resolves to "not-a-real-dataset-id" — the same "declared-but-unused count field is a
        // no-op" convention errorCountField/warningCountField/acceptedCountField already follow,
        // now also covering "the datasetId itself doesn't match anything on this instance".
        var (engine, fileStorage, _) = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, "alice", Profile);

        var result = engine.SyncBulkDatasetSyncState(started.InstanceId, TenantId, "alice", Profile, "not-a-real-dataset-id");

        result.ResponseState.Should().Be("render");
    }
}
