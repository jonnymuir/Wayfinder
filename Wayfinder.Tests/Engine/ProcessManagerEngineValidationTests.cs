using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Proves <see cref="ProcessManagerEngine.Advance"/> itself rejects a tampered submission —
/// unknown field keys, out-of-allowlist options, out-of-range values, missing required fields —
/// with no host-side wiring required. See docs/guides and the "watertight" plan this covers.
/// </summary>
public class ProcessManagerEngineValidationTests
{
    private const string DefinitionKey = "validation-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "validation-test",
          "displayName": "Validation Test",
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
                    { "type": "text", "fieldKey": "name", "label": "Name", "required": true, "maxLength": 10 },
                    { "type": "radio", "fieldKey": "colour", "label": "Colour", "required": true, "options": ["red", "blue"] },
                    { "type": "number", "fieldKey": "amount", "label": "Amount", "required": true, "min": 1, "max": 100 }
                  ]
                }
              ],
              "routes": [
                { "id": "start--continue", "target": "to-done", "trigger": "continue", "label": "Continue" }
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
              "key": "to-done",
              "displayName": "To done",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-done--continue", "target": "done", "trigger": "continue" }
              ]
            }
          ]
        }
        """;

    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) StartInstance()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughSanitizer());

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);
        return (engine, started.InstanceId, started.StateVersion);
    }

    private static Dictionary<string, object?> ValidFieldValues() => new()
    {
        ["name"] = "Alice",
        ["colour"] = "red",
        ["amount"] = 10,
    };

    [Fact]
    public void Advance_AcceptsWellFormedSubmission()
    {
        var (engine, instanceId, stateVersion) = StartInstance();

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, ValidFieldValues());

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_RejectsInjectedFieldKeyNotDeclaredOnCurrentStage()
    {
        var (engine, instanceId, stateVersion) = StartInstance();
        var tampered = ValidFieldValues();
        tampered["injectedFutureStageField"] = "hacked";

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, tampered);

        Assert.Contains(result.Problems, p => p.FieldKey == "injectedFutureStageField");
        // Rejected before advancing — still on the start stage, not Done.
        Assert.Equal("Start", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_RejectsMissingRequiredField()
    {
        var (engine, instanceId, stateVersion) = StartInstance();
        var tampered = ValidFieldValues();
        tampered.Remove("name");

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, tampered);

        Assert.Contains(result.Problems, p => p.FieldKey == "name");
    }

    [Fact]
    public void Advance_RejectsOptionOutsideDeclaredAllowlist()
    {
        var (engine, instanceId, stateVersion) = StartInstance();
        var tampered = ValidFieldValues();
        tampered["colour"] = "purple";

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, tampered);

        Assert.Contains(result.Problems, p => p.FieldKey == "colour");
    }

    [Fact]
    public void Advance_RejectsValueOutsideDeclaredRange()
    {
        var (engine, instanceId, stateVersion) = StartInstance();
        var tampered = ValidFieldValues();
        tampered["amount"] = 9999;

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, tampered);

        Assert.Contains(result.Problems, p => p.FieldKey == "amount");
    }

    [Fact]
    public void Advance_RejectsValueExceedingDeclaredMaxLength()
    {
        var (engine, instanceId, stateVersion) = StartInstance();
        var tampered = ValidFieldValues();
        tampered["name"] = "this name is far too long";

        var result = engine.Advance(
            instanceId, TenantId, UserId, Wayfinder.Models.ServiceDesign.ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, tampered);

        Assert.Contains(result.Problems, p => p.FieldKey == "name");
    }

    private sealed class PassthroughSanitizer : IServiceContentSanitizer
    {
        public string Sanitize(string? html) => html ?? string.Empty;
    }
}
