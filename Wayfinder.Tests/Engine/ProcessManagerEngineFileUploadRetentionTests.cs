using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A file-upload field's value can never be resubmitted by a browser the way a text/radio/date
/// field's value is (an <c>&lt;input type="file"&gt;</c> can't be pre-filled) — a host that
/// already has a value for the field is expected to leave its key out of the fieldValues it posts
/// entirely, relying on the field's already-persisted value to satisfy Required. Advancing off a
/// stage a visitor is revisiting (e.g. via a "change:" jump back from Check Answers) must honour
/// that already-persisted value instead of treating an omitted key as "no answer" — see
/// ProcessManagerEngine.Advance's pre-transition FieldValueValidator.Validate call.
/// </summary>
public class ProcessManagerEngineFileUploadRetentionTests
{
    private const string DefinitionKey = "file-upload-retention-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "file-upload-retention-test",
          "displayName": "File Upload Retention Test",
          "version": 1,
          "initialStage": "upload",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "stages": [
            {
              "stageKey": "upload",
              "displayName": "Upload your evidence",
              "queueKey": "citizen",
              "components": [
                {
                  "type": "fieldset",
                  "legend": "Evidence",
                  "children": [
                    { "type": "file-upload", "fieldKey": "evidence", "label": "Evidence", "required": true }
                  ]
                }
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
                    { "type": "file-upload", "fieldKey": "evidence", "label": "Evidence", "required": true, "changeStateKey": "upload" }
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
    public void Advance_RevisitingUploadStageWithoutReselectingFile_RetainsThePersistedReference()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);
        var atCheckAnswers = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["evidence"] = "evidence.pdf" });

        Assert.Empty(atCheckAnswers.Problems);
        Assert.Equal("Check your answers", atCheckAnswers.Render?.StateDisplayName);

        // "Change" link back to the upload stage — same jump a summary-list's own Change button
        // triggers via the "change:" action prefix.
        var backAtUpload = engine.Advance(
            atCheckAnswers.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "change:upload", atCheckAnswers.StateVersion, fieldValues: null);

        Assert.Empty(backAtUpload.Problems);
        Assert.Equal("Upload your evidence", backAtUpload.Render?.StateDisplayName);

        // Continue again without reselecting a file — the host leaves "evidence" out of
        // fieldValues entirely, exactly as it would for a real unresubmitted file input.
        var afterContinue = engine.Advance(
            backAtUpload.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", backAtUpload.StateVersion, fieldValues: null);

        Assert.Empty(afterContinue.Problems);
        Assert.Equal("Check your answers", afterContinue.Render?.StateDisplayName);
    }
}
