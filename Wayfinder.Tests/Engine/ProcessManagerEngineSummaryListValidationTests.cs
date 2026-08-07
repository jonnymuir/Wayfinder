using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A summary-list echoes fields already collected (and validated) on the earlier stages its
/// "change" links point back to. Advancing FROM the stage that renders it (e.g. a check-answers
/// page's own "submit", with no fields of its own) must never demand those echoed fields be
/// resubmitted — see ProcessManagerEngine.BuildComponents' SummaryListComponent case.
/// </summary>
public class ProcessManagerEngineSummaryListValidationTests
{
    private const string DefinitionKey = "check-answers-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "check-answers-test",
          "displayName": "Check Answers Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "citizen",
              "components": [
                {
                  "type": "fieldset",
                  "legend": "Details",
                  "children": [
                    { "type": "text", "fieldKey": "name", "label": "Name", "required": true }
                  ]
                }
              ],
              "routes": [
                { "id": "start--continue", "target": "to-check-answers", "trigger": "continue", "label": "Continue" }
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
                    { "type": "text", "fieldKey": "name", "label": "Name", "required": true, "changeStateKey": "start" }
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
    public void Advance_SubmitFromCheckAnswers_DoesNotDemandEchoedSummaryListFieldsBeResubmitted()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);
        var atCheckAnswers = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["name"] = "Alice" });

        Assert.Empty(atCheckAnswers.Problems);
        Assert.Equal("Check your answers", atCheckAnswers.Render?.StateDisplayName);

        // Submit with no fields of its own — the "name" field is only ever echoed here via the
        // summary-list, never re-collected on this stage.
        var afterSubmit = engine.Advance(
            atCheckAnswers.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "submit", atCheckAnswers.StateVersion, fieldValues: null);

        Assert.Empty(afterSubmit.Problems);
        Assert.Equal("Done", afterSubmit.Render?.StateDisplayName);
    }
}
