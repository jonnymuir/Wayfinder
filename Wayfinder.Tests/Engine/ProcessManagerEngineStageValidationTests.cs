using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Proves <c>StageDefinition.Validations</c> — the declarative alternative to a host overriding
/// <c>ProcessManagerEngine.ValidateAdvance</c> with bespoke C# — is enforced server-side inside
/// <see cref="ProcessManagerEngine.Advance"/> itself. The gate field (<c>hasIssue</c>) is captured
/// on an earlier stage than the rule that reads it (<c>notes</c>), proving a rule can reference
/// any earlier-captured field with no extra wiring.
/// </summary>
public class ProcessManagerEngineStageValidationTests
{
    private const string DefinitionKey = "stage-validation-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "stage-validation-test",
          "displayName": "Stage Validation Test",
          "version": 1,
          "initialStage": "details",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "calculations": {
            "fields": {
              "hasEvidence": { "expr": "matches(notes, '\\d')" }
            }
          },
          "stages": [
            {
              "stageKey": "details",
              "displayName": "Details",
              "queueKey": "citizen",
              "components": [
                { "type": "boolean", "fieldKey": "hasIssue", "label": "Reporting an issue", "default": "false" }
              ],
              "routes": [
                { "id": "details--continue", "target": "to-notes", "trigger": "continue", "label": "Continue" }
              ]
            },
            {
              "stageKey": "notes",
              "displayName": "Notes",
              "queueKey": "citizen",
              "components": [
                { "type": "text", "fieldKey": "notes", "label": "Notes", "default": "" }
              ],
              "validations": [
                {
                  "code": "evidence-required",
                  "when": "hasIssue",
                  "rule": "hasEvidence",
                  "field": "notes",
                  "message": "Include a concrete detail (e.g. a reference number) in your notes."
                }
              ],
              "routes": [
                { "id": "notes--continue", "target": "to-done", "trigger": "continue", "label": "Continue" }
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
              "key": "to-notes",
              "displayName": "Continue to notes",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-notes--continue", "target": "notes", "trigger": "continue" }
              ]
            },
            {
              "key": "to-done",
              "displayName": "Continue to done",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-done--continue", "target": "done", "trigger": "continue" }
              ]
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

    /// <summary>Starts an instance and advances it to the "notes" stage with the given hasIssue value.</summary>
    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) ArriveAtNotes(bool hasIssue)
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        var atNotes = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["hasIssue"] = hasIssue });

        Assert.Equal("Notes", atNotes.Render?.StateDisplayName);
        return (engine, started.InstanceId, atNotes.StateVersion);
    }

    [Fact]
    public void Advance_BlocksWhenGateAppliesAndRuleFails()
    {
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "no digits here" });

        Assert.Contains(result.Problems, p => p.FieldKey == "notes" && p.Code == "evidence-required");
        // Rejected before advancing — still on "notes", never saved past it.
        Assert.Equal("Notes", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_AllowsWhenGateAppliesAndRulePasses()
    {
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "see ref 42" });

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_SkipsRuleWhenGateDoesNotApply()
    {
        // hasIssue captured false on the EARLIER "details" stage — the rule on "notes" must read
        // it from there, and skip entirely, regardless of what "notes" itself contains.
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: false);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "no digits here" });

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }
}
