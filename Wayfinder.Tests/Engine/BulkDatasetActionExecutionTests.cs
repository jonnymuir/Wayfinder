using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Covers the engine actually executing <c>bulk-dataset-ingest</c>/<c>bulk-dataset-materialize</c>
/// end to end, on top of the existing support-system-call machinery
/// (<see cref="SupportSystemActionExecutionTests"/>): a file is submitted, an external system's
/// response is ingested the moment the join gateway releases onto the review stage, and — the
/// whole point of the loop design in docs/guides/bulk-data-review.md — resubmitting materializes
/// the *previous round's ingested dataset*, not the original upload, into the field the external
/// system reads next.
/// </summary>
public class BulkDatasetActionExecutionTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string SupportSystemKey = "safetynet-underwriting";
    private const string CapabilityKey = "validate-contributions-file";
    private const string DefinitionKey = "bulk-dataset-test";

    private const string Columns = "memberRef,memberName,errorText";

    private static readonly ActorProfile OperationsProfile = new()
    {
        VisibleQueues = ["operations"],
        StartableQueues = ["operations"],
        ActionableQueues = ["operations"],
        RestrictToInstanceOwner = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "bulk-dataset-test",
          "displayName": "Bulk Dataset Test",
          "version": 1,
          "initialStage": "upload",
          "requestPolicy": "single",
          "queues": [
            { "key": "operations", "displayName": "Operations", "actor": "caseworker" },
            { "key": "automation", "displayName": "Automation", "actor": "system" }
          ],
          "stages": [
            {
              "stageKey": "upload",
              "displayName": "Upload",
              "queueKey": "operations",
              "components": [ { "type": "file-upload", "fieldKey": "contributionsFile", "label": "File", "required": true } ],
              "routes": [ { "id": "upload--submit--split", "target": "to-support-system", "trigger": "submit" } ]
            },
            {
              "stageKey": "automation",
              "displayName": "Automation",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "Processing" } ],
              "actions": [
                {
                  "type": "bulk-dataset-materialize",
                  "timing": "onEnter",
                  "params": { "datasetIdField": "contributionsDatasetId", "targetFileField": "contributionsFile" }
                },
                {
                  "type": "support-system-call",
                  "timing": "onEnter",
                  "params": {
                    "supportSystemKey": "safetynet-underwriting",
                    "capabilityKey": "validate-contributions-file",
                    "inputs": { "file": "contributionsFile" }
                  }
                }
              ],
              "routes": [ { "id": "automation--processed--join", "target": "check-complete", "trigger": "processed" } ]
            },
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "operations",
              "components": [ { "type": "text", "fieldKey": "reviewNotes", "label": "Notes", "required": false } ],
              "actions": [
                {
                  "type": "bulk-dataset-ingest",
                  "timing": "onEnter",
                  "params": {
                    "sourceFileField": "contributionsResponseFile",
                    "datasetIdField": "contributionsDatasetId",
                    "errorCountField": "contributionsErrorCount",
                    "warningCountField": "contributionsWarningCount",
                    "acceptedCountField": "contributionsAcceptedCount",
                    "columns": [
                      { "key": "memberRef", "title": "Ref", "valueKind": "String", "role": "RowKey" },
                      { "key": "memberName", "title": "Name", "valueKind": "String", "role": "Data", "editable": true },
                      { "key": "errorText", "title": "Errors", "valueKind": "String", "role": "ResponseError" }
                    ]
                  }
                }
              ],
              "routes": [ { "id": "review--resubmit--split", "target": "to-support-system", "trigger": "resubmit" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-support-system",
              "displayName": "Send to support system",
              "gatewayType": "Split",
              "queueKey": "operations",
              "routes": [
                { "id": "to-support-system--submit--join", "target": "check-complete", "trigger": "submit" },
                { "id": "to-support-system--submit--automation", "target": "automation", "trigger": "submit" },
                { "id": "to-support-system--resubmit--join", "target": "check-complete", "trigger": "resubmit" },
                { "id": "to-support-system--resubmit--automation", "target": "automation", "trigger": "resubmit" }
              ]
            },
            {
              "key": "check-complete",
              "displayName": "Check complete",
              "gatewayType": "Join",
              "queueKey": "operations",
              "waitingContent": "Processing your file.",
              "waitingPollIntervalMs": 2000,
              "routes": [ { "id": "check-complete--processed--review", "target": "review", "trigger": "processed" } ],
              "requiredIncomingQueues": ["operations", "automation"]
            }
          ]
        }
        """;

    /// <summary>
    /// Records every file it was actually asked to read (so a test can prove a resubmission sent
    /// the materialized dataset, not the original upload) and, on every poll, resolves with a
    /// freshly-saved response file whose content is whatever the test currently has queued in
    /// <see cref="NextResponseCsv"/>.
    /// </summary>
    private sealed class ScriptedBulkSupportSystemClient(IServiceRequestFileStorage fileStorage) : ISupportSystemClient
    {
        private readonly Dictionary<string, string> _instanceIdByExternalRef = new();

        public string SupportSystemKey => BulkDatasetActionExecutionTests.SupportSystemKey;
        public List<string?> ReceivedFileContents { get; } = [];
        public string NextResponseCsv { get; set; } = "";

        /// <summary>False makes <see cref="CheckStatusAsync"/> report "still pending" — the test
        /// flips this once it wants the queued <see cref="NextResponseCsv"/> to actually resolve.</summary>
        public bool ReadyToResolve { get; set; }

        public async Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey,
            IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context,
            CancellationToken ct = default)
        {
            var fileReference = inputs["file"].FileReference;
            if (fileReference is not null)
            {
                var stream = await fileStorage.OpenReadAsync(fileReference.StorageKey, ct);
                using var reader = new StreamReader(stream!);
                ReceivedFileContents.Add(await reader.ReadToEndAsync(ct));
            }
            else
            {
                ReceivedFileContents.Add(null);
            }

            var externalReference = "ext-" + ReceivedFileContents.Count;
            _instanceIdByExternalRef[externalReference] = context.InstanceId;
            return new SupportSystemInvocationReceipt { ExternalReference = externalReference };
        }

        public async Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey, SupportSystemInvocationReceipt receipt, CancellationToken ct = default)
        {
            if (!ReadyToResolve)
            {
                return null;
            }

            var instanceId = _instanceIdByExternalRef[receipt.ExternalReference];
            await using var responseStream = new MemoryStream(Encoding.UTF8.GetBytes(NextResponseCsv));
            var storageKey = await fileStorage.SaveAsync(instanceId, "contributionsResponseFile", responseStream, "response.csv", ct);
            var fileReference = new ServiceRequestFileReference
            {
                StorageKey = storageKey,
                OriginalFileName = "response.csv",
                ContentType = "text/csv",
                SizeBytes = NextResponseCsv.Length,
            };

            return new SupportSystemOutcome
            {
                OutcomeKey = "processed",
                ResultPayload = new JsonObject { ["contributionsResponseFile"] = JsonSerializer.SerializeToNode(fileReference) },
            };
        }
    }

    private static SupportSystemDescriptor FixtureDescriptor() => new()
    {
        Key = SupportSystemKey,
        DisplayName = "SafetyNet Underwriting",
        Capabilities =
        [
            new SupportSystemCapabilityDescriptor
            {
                Key = CapabilityKey,
                DisplayName = "Validate a contributions file",
                Inputs = [new() { Key = "file", Title = "File", ValueKind = Wayfinder.Models.ServiceDesign.Components.ComponentPropertyValueKind.String, Format = "field-ref" }],
                Outputs = [new() { Key = "contributionsResponseFile", Title = "Response file", ValueKind = Wayfinder.Models.ServiceDesign.Components.ComponentPropertyValueKind.String }],
                SupportedCompletionModes = [SupportSystemCompletionMode.Poll],
                Outcomes = [new() { Key = "processed", DisplayName = "Processed" }],
            },
        ],
    };

    [Fact]
    public async Task FullLoop_IngestsResponseOnJoinRelease_ThenMaterializesLastRoundsDatasetOnResubmit()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(FixtureDescriptor());

            var fileStorage = new InMemoryServiceRequestFileStorage();
            var bulkDatasetStore = new InMemoryBulkDatasetStore(fileStorage);
            var client = new ScriptedBulkSupportSystemClient(fileStorage)
            {
                NextResponseCsv = string.Join('\n', Columns, "NJF-001,Alice,", "NJF-002,Bob,Missing DOB"),
            };

            var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
            var engine = new ProcessManagerEngine(
                NullLogger.Instance,
                new SingleDefinitionServiceBlueprintStore(definition),
                new PassthroughContentSanitizer(),
                supportSystemClients: [client],
                bulkDatasetStore: bulkDatasetStore);

            var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, OperationsProfile);

            const string originalUploadMarker = "ORIGINAL-UPLOAD-MARKER";
            var originalCsv = string.Join('\n', "memberRef,memberName", $"NJF-001,{originalUploadMarker}");
            await using var originalStream = new MemoryStream(Encoding.UTF8.GetBytes(originalCsv));
            var originalStorageKey = await fileStorage.SaveAsync(started.InstanceId, "contributionsFile", originalStream, "contributions.csv");
            var originalFileReference = new ServiceRequestFileReference
            {
                StorageKey = originalStorageKey,
                OriginalFileName = "contributions.csv",
                ContentType = "text/csv",
                SizeBytes = originalCsv.Length,
            };

            // "operations" declares no AssignmentPolicy — pickup is still mandatory (see
            // docs/guides/work-allocation.md), same as any other shared queue.
            var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, OperationsProfile);

            // Round 1: submits the real upload — bulk-dataset-materialize is a no-op (no dataset
            // id exists yet), so the client should see exactly the original upload's bytes.
            var afterSplit = engine.Advance(
                started.InstanceId, TenantId, UserId, OperationsProfile, "submit", pickedUp.StateVersion,
                new Dictionary<string, object?> { ["contributionsFile"] = originalFileReference });

            afterSplit.ResponseState.Should().Be("defer");
            client.ReceivedFileContents.Should().ContainSingle();
            client.ReceivedFileContents[0].Should().Contain(originalUploadMarker);

            // Poll resolves the join (client.CheckStatusAsync fires, feeding back the response
            // file) and releases straight onto "review", whose own onEnter bulk-dataset-ingest
            // action should fire immediately.
            client.ReadyToResolve = true;
            var resolved = engine.GetCurrent(DefinitionKey, TenantId, UserId, OperationsProfile, afterSplit.InstanceId);

            resolved.ResponseState.Should().Be("render");
            resolved.Render!.StateDisplayName.Should().Be("Review");

            var instanceAfterRound1 = engine.GetAllInstances().Single(i => i.InstanceId == afterSplit.InstanceId);
            instanceAfterRound1.FieldValues["contributionsErrorCount"].Should().Be(1);
            instanceAfterRound1.FieldValues["contributionsWarningCount"].Should().Be(0);
            instanceAfterRound1.FieldValues["contributionsAcceptedCount"].Should().Be(1);
            var datasetId = instanceAfterRound1.FieldValues["contributionsDatasetId"].Should().BeOfType<string>().Subject;

            var summary = await bulkDatasetStore.GetSummaryAsync(afterSplit.InstanceId, datasetId);
            summary!.TotalRowCount.Should().Be(2);

            // Round 2: resubmit with no new upload — bulk-dataset-materialize should now find
            // round 1's dataset id and feed the client round 1's *ingested response*, not the
            // original upload.
            client.ReadyToResolve = false;
            client.NextResponseCsv = string.Join('\n', Columns, "NJF-001,Alice,", "NJF-002,Bob,");

            // The Split/Join round trip always fans into a fresh, unpicked cursor on arrival at
            // "review" — must be picked up again before "resubmit" is actionable.
            var reviewItem = engine.GetQueueWorkItems(TenantId, UserId, OperationsProfile).Items.Single(i => i.InstanceId == resolved.InstanceId);
            var reviewPickedUp = engine.PickupWorkItem(resolved.InstanceId, reviewItem.CursorId, TenantId, UserId, OperationsProfile);

            var afterResubmit = engine.Advance(
                resolved.InstanceId, TenantId, UserId, OperationsProfile, "resubmit", reviewPickedUp.StateVersion, null);

            afterResubmit.ResponseState.Should().Be("defer");
            client.ReceivedFileContents.Should().HaveCount(2);
            client.ReceivedFileContents[1].Should().NotContain(originalUploadMarker);
            client.ReceivedFileContents[1].Should().Contain("Missing DOB");

            // Round 2's own response resolves the join and ingests fresh — the error clears now
            // the response no longer flags row 2.
            client.ReadyToResolve = true;
            var resolvedAgain = engine.GetCurrent(DefinitionKey, TenantId, UserId, OperationsProfile, afterResubmit.InstanceId);
            var instanceAfterRound2 = engine.GetAllInstances().Single(i => i.InstanceId == afterSplit.InstanceId);
            instanceAfterRound2.FieldValues["contributionsErrorCount"].Should().Be(0);
            instanceAfterRound2.FieldValues["contributionsDatasetId"].Should().NotBe(datasetId, "round 2 ingest mints a fresh dataset id");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
