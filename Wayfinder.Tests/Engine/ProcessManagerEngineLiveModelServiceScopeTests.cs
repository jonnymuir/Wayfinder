using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A host's serviceInputsResolver can reasonably return any plain CLR numeric type for a
/// service-sourced calculation field — nothing in that delegate's own contract
/// (<c>IReadOnlyDictionary&lt;string, object?&gt;</c>) restricts it to <c>decimal</c>
/// specifically. Found live: a host supplying a plain <c>int</c> nested inside a service field's
/// object value (e.g. <c>member.age</c>) got it silently stringified into the
/// <c>[data-wayfinder-live-model]</c> JSON embedded in the page — <c>ScopeValueToJson</c>'s own
/// switch only special-cased <c>decimal</c>, so an <c>int</c> fell through to the
/// <c>value.ToString()</c> default, producing a JSON string ("47") instead of a JSON number
/// (47). The client's own <c>toScope</c>/calculation engine (UmbracoPrism.Client) only
/// type-converts genuine JSON numbers into evaluator <c>Dec</c> values, so every downstream
/// expression referencing that field (e.g. <c>max(55, member.age + 1)</c>) threw
/// "Expected a number but got '47'" the moment the client tried to re-evaluate anything — which
/// silently aborted the client-side live-form update entirely, leaving whatever the server
/// happened to render (including any chart/stat-card values) stuck, uncorrected, for the rest of
/// the page's life.
/// </summary>
public class ProcessManagerEngineLiveModelServiceScopeTests
{
    private const string DefinitionKey = "live-model-service-scope-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "live-model-service-scope-test",
          "displayName": "Live Model Service Scope Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "calculations": {
            "fields": {
              "member": { "source": "service" },
              "minAge": { "expr": "max(55, member.age + 1)" }
            }
          },
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "citizen",
              "components": [
                { "type": "panel", "heading": "Start" }
              ],
              "routes": []
            }
          ],
          "gateways": []
        }
        """;

    private static ProcessManagerEngine BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            (_, _, _) => new Dictionary<string, object?>
            {
                // A plain int, exactly as a host's own C# service-record type (age: int) would
                // supply it — not pre-cast to decimal, which is the whole point of this test.
                ["member"] = new Dictionary<string, object?> { ["age"] = 47 }
            });
    }

    [Fact]
    public void BuildLiveModel_EmitsIntServiceFieldAsJsonNumber_NotStringifiedText()
    {
        var engine = BuildEngine();

        var result = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        var live = Assert.IsType<JsonObject>(result.Render?.Data?["live"]);
        var service = Assert.IsType<JsonObject>(live["service"]);
        var member = Assert.IsType<JsonObject>(service["member"]);
        var age = Assert.IsType<JsonValue>(member["age"], exactMatch: false);

        Assert.Equal(JsonValueKind.Number, age.GetValueKind());
        Assert.Equal(47m, age.GetValue<decimal>());
    }
}
