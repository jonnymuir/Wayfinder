using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// An input's <c>defaultFrom</c> can name a <c>source: "service"</c> value the host supplies, so a
/// blueprint can pre-fill a field from whoever is signed in without any per-blueprint code. A
/// dotted name reads into an object-valued service field.
/// </summary>
public class ProcessManagerEngineDefaultFromServiceTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string BlueprintJson(string defaultFrom) => $$"""
        {
          "definitionKey": "default-from-service-test",
          "displayName": "Default From Service Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "calculations": {
            "fields": {
              "user": { "source": "service" },
              "signedInName": { "source": "service", "valueKind": "string" }
            }
          },
          "queues": [ { "key": "citizen", "displayName": "Applicant", "actor": "citizen" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "citizen",
              "components": [
                { "type": "text", "fieldKey": "applicantName", "label": "Name", "defaultFrom": "{{defaultFrom}}" }
              ],
              "routes": []
            }
          ],
          "gateways": []
        }
        """;

    private static string? RenderedDefault(string defaultFrom)
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson(defaultFrom), JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            (_, _, _) => new Dictionary<string, object?>
            {
                ["user"] = new Dictionary<string, object?> { ["name"] = "Alex Applicant", ["email"] = "alex@example.com" },
                ["signedInName"] = "Alex Applicant"
            });

        var result = engine.GetCurrent("default-from-service-test", TenantId, UserId);

        return result.Render!.Components
            .SelectMany(c => c.Fields)
            .Single(f => f.FieldKey == "applicantName")
            .Value?.ToString();
    }

    [Fact]
    public void DefaultFrom_DottedPathIntoAnObjectServiceField_PrefillsTheInput() =>
        Assert.Equal("Alex Applicant", RenderedDefault("user.name"));

    [Fact]
    public void DefaultFrom_NamingAScalarServiceField_PrefillsTheInput() =>
        Assert.Equal("Alex Applicant", RenderedDefault("signedInName"));

    [Fact]
    public void DefaultFrom_PathThatStopsAtAnObject_PrefillsNothing() =>
        Assert.Null(RenderedDefault("user"));

    [Fact]
    public void DefaultFrom_PathThatDoesNotExistInTheObject_PrefillsNothing() =>
        Assert.Null(RenderedDefault("user.phone"));
}
