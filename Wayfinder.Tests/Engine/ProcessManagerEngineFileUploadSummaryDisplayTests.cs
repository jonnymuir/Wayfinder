using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A file-upload field's stored value is a <see cref="ServiceRequestFileReference"/> — GOV.UK's
/// own "check your answers" convention is one summary row per answer, with no requirement that a
/// summary-list child's own declared type match the field it echoes. Confirmed live: a real
/// MCP-authored blueprint declared such a child as plain "text" rather than "file-upload", so the
/// file-upload-aware display extraction (gated on the CHILD's own declared type) never ran, and a
/// citizen's own "check your answers" page rendered the raw stored reference JSON
/// ({"StorageKey":...,"OriginalFileName":...}) instead of the filename.
/// </summary>
public class ProcessManagerEngineFileUploadSummaryDisplayTests
{
    private const string DefinitionKey = "file-upload-summary-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // The summary-list child is deliberately "text", not "file-upload" — reproducing the real
    // authoring mistake exactly, not a hypothetical one.
    private const string BlueprintJson = """
        {
          "definitionKey": "file-upload-summary-test",
          "displayName": "File Upload Summary Test",
          "version": 1,
          "initialStage": "upload",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "stages": [
            {
              "stageKey": "upload",
              "displayName": "Upload evidence",
              "queueKey": "citizen",
              "components": [
                { "type": "file-upload", "fieldKey": "evidence", "label": "Evidence", "required": true }
              ],
              "routes": [
                { "id": "upload--continue", "target": "to-check-answers", "trigger": "continue", "label": "Continue" }
              ]
            },
            {
              "stageKey": "check-answers",
              "displayName": "Check your answers",
              "queueKey": "citizen",
              "components": [
                {
                  "type": "summary-list",
                  "children": [
                    { "type": "text", "fieldKey": "evidence", "label": "Evidence", "required": false }
                  ]
                }
              ],
              "routes": [
                { "id": "check-answers--submit", "target": "to-done", "trigger": "submit", "label": "Submit" }
              ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "citizen",
              "components": [
                { "type": "panel", "heading": "Done" }
              ]
            }
          ],
          "gateways": [
            {
              "key": "to-check-answers",
              "displayName": "To check answers",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-check-answers--continue", "target": "check-answers", "trigger": "continue" }
              ]
            },
            {
              "key": "to-done",
              "displayName": "To done",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-done--submit", "target": "done", "trigger": "submit" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void SummaryListRow_DeclaredAsText_ForAFileUploadFieldsValue_StillShowsTheFilename()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        // A JsonElement is exactly the CLR shape a real DB-backed store round-trips a
        // ServiceRequestFileReference into on reload (FieldValues has no custom converter) —
        // constructing it directly here reproduces that without needing a real database.
        var reference = new ServiceRequestFileReference
        {
            StorageKey = "abc/def.pdf",
            OriginalFileName = "juggling-licence-evidence.pdf",
            ContentType = "application/pdf",
            SizeBytes = 192
        };
        var reloadedShapeValue = JsonSerializer.SerializeToElement(reference);

        var atCheckAnswers = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion,
            new Dictionary<string, object?> { ["evidence"] = reloadedShapeValue });

        Assert.Empty(atCheckAnswers.Problems);
        Assert.Equal("Check your answers", atCheckAnswers.Render?.StateDisplayName);

        var summaryField = atCheckAnswers.Render!.Components
            .Single(c => c.Type == "summary-list").Fields
            .Single(f => f.FieldKey == "evidence");

        Assert.Equal("text", summaryField.FieldType);
        Assert.Equal("juggling-licence-evidence.pdf", summaryField.Value?.ToString());
        Assert.DoesNotContain("StorageKey", summaryField.Value?.ToString() ?? "");
    }
}
