using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Proves <see cref="ServiceBlueprintRouteDefinition.ShowWhen"/> — a route whose ShowWhen
/// evaluates false is excluded from <see cref="StepContent.AvailableActions"/> entirely, not
/// merely disabled — the mechanism that replaced an always/event/guard route-condition UI that
/// looked functional but was never evaluated anywhere and (via a client/server wire-key mismatch)
/// didn't even persist across a save. See JugglingLicenceStageValidationTests for the same
/// mechanism exercised against the real "under-review" stage it was actually built for.
///
/// The gate field (<c>hasFile</c>) is captured on an earlier stage ("capture") than the routes
/// that read it ("review"), the same cross-stage shape the real blueprint uses — proving a route's
/// ShowWhen can reference any field captured earlier in the journey with no extra wiring, exactly
/// like a stage validation's <c>when</c>/<c>rule</c> already can.
/// </summary>
public class ProcessManagerEngineRouteShowWhenTests
{
    private const string DefinitionKey = "route-show-when-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "route-show-when-test",
          "displayName": "Route ShowWhen Test",
          "version": 1,
          "initialStage": "capture",
          "requestPolicy": "single",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "capture",
              "displayName": "Capture",
              "queueKey": "caseworker",
              "components": [
                { "type": "boolean", "fieldKey": "hasFile", "label": "Has file", "default": "false" }
              ],
              "routes": [
                { "id": "capture--continue", "target": "to-review", "trigger": "continue" }
              ]
            },
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "caseworker",
              "routes": [
                { "id": "review--with-file", "target": "to-with-file", "trigger": "with-file", "showWhen": "hasFile" },
                { "id": "review--without-file", "target": "to-without-file", "trigger": "without-file", "showWhen": "not hasFile" }
              ]
            },
            {
              "stageKey": "withFileDone",
              "displayName": "With file done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "With file done" } ]
            },
            {
              "stageKey": "withoutFileDone",
              "displayName": "Without file done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Without file done" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-review",
              "displayName": "To review",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-review--continue", "target": "review", "trigger": "continue" } ]
            },
            {
              "key": "to-with-file",
              "displayName": "To with-file done",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-with-file--with-file", "target": "withFileDone", "trigger": "with-file" } ]
            },
            {
              "key": "to-without-file",
              "displayName": "To without-file done",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-without-file--without-file", "target": "withoutFileDone", "trigger": "without-file" } ]
            }
          ]
        }
        """;

    private static ProcessManagerEngine BuildEngine(string? blueprintJson = null)
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(blueprintJson ?? BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) ArriveAtReview(bool hasFile)
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        var atReview = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["hasFile"] = hasFile });

        Assert.Equal("Review", atReview.Render?.StateDisplayName);
        return (engine, started.InstanceId, atReview.StateVersion);
    }

    [Fact]
    public void WithFileCaptured_OnlyTheWithFileRouteIsOffered()
    {
        var (engine, instanceId, _) = ArriveAtReview(hasFile: true);

        var current = engine.GetCurrent(DefinitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner, instanceId);

        var actionKeys = current.Render?.AvailableActions.Select(a => a.ActionKey).ToArray() ?? [];
        Assert.Contains("with-file", actionKeys);
        Assert.DoesNotContain("without-file", actionKeys);
    }

    [Fact]
    public void WithoutFileCaptured_OnlyTheWithoutFileRouteIsOffered()
    {
        var (engine, instanceId, _) = ArriveAtReview(hasFile: false);

        var current = engine.GetCurrent(DefinitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner, instanceId);

        var actionKeys = current.Render?.AvailableActions.Select(a => a.ActionKey).ToArray() ?? [];
        Assert.Contains("without-file", actionKeys);
        Assert.DoesNotContain("with-file", actionKeys);
    }

    [Fact]
    public void HiddenRouteIsGenuinelyUnreachable_NotJustUnlisted()
    {
        var (engine, instanceId, stateVersion) = ArriveAtReview(hasFile: false);

        // "with-file" is hidden while hasFile is false — submitting its trigger directly
        // (bypassing whatever UI would have hidden the button) must be rejected the same way any
        // other action absent from AvailableActions already is.
        var tampered = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "with-file", stateVersion, null);

        Assert.Equal("INVALID_TRANSITION", tampered.Problems.Single().Code);
    }

    [Fact]
    public void VisibleRouteStillAdvancesNormally()
    {
        var (engine, instanceId, stateVersion) = ArriveAtReview(hasFile: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "with-file", stateVersion, null);

        Assert.Empty(result.Problems);
        Assert.Equal("With file done", result.Render?.StateDisplayName);
    }

    [Fact]
    public void UnparsableShowWhen_FailsOpen_RouteStaysAvailable()
    {
        // Same fail-open bias as Component.ShowWhen: a blueprint that would never have reached
        // this state through the editor (ServiceBlueprintAuthoringService.Validate blocks a
        // malformed ShowWhen at save time) must still degrade safely if it somehow does.
        var brokenJson = BlueprintJson.Replace(
            "\"showWhen\": \"hasFile\"", "\"showWhen\": \"not a valid expression (((\"");
        var engine = BuildEngine(brokenJson);
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        var atReview = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["hasFile"] = false });

        var actionKeys = atReview.Render?.AvailableActions.Select(a => a.ActionKey).ToArray() ?? [];
        Assert.Contains("with-file", actionKeys);
    }
}
