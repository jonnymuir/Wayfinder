using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Rendering.GovUk;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// A stat tile with <c>display: "map"</c> shows a read-only map of a "latitude,longitude" point (what a
/// location-picker captures). The point is always written out as text as well, so the tile still reads
/// without the script and to a screen reader.
/// </summary>
public class StatGroupMapTests
{
    private static readonly GovUkComponentRenderer Renderer = new();
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string Render(StatItem stat)
    {
        var step = new StepContent
        {
            StepType = "question",
            StateDisplayName = "Test",
            Components = [new ComponentRenderPayload { Type = "stat-group", Stats = [stat] }],
            AvailableActions = [],
        };
        return Renderer.RenderForm(step, [], "/test", 0);
    }

    [Fact]
    public void AMapTile_RendersAMapHookCarryingThePointAndTheLabel()
    {
        var html = Render(new StatItem { Label = "Where", FieldKey = "location", Value = "53.792035,-1.663200", Display = "map" });

        Assert.Contains("data-wayfinder-location-map", html);
        Assert.Contains("""data-wayfinder-location="53.792035,-1.663200" """.TrimEnd(), html);
        Assert.Contains("""data-wayfinder-label="Where" """.TrimEnd(), html);
        Assert.Contains("wayfinder-stat-card--map", html);
    }

    [Fact]
    public void AMapTile_StillWritesTheValueOutAsText()
    {
        var html = Render(new StatItem { Label = "Where", FieldKey = "location", Value = "53.792035,-1.663200", Display = "map" });

        Assert.Contains("""<div class="wayfinder-stat-card__value wayfinder-stat-card__value--point">53.792035,-1.663200</div>""", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text")]
    public void ATileThatIsNotAMap_RendersNoMapHook(string? display)
    {
        var html = Render(new StatItem { Label = "Where", FieldKey = "location", Value = "53.792035,-1.663200", Display = display });

        Assert.DoesNotContain("data-wayfinder-location-map", html);
        Assert.DoesNotContain("wayfinder-stat-card--map", html);
    }

    [Fact]
    public void AMapTileWithNoValueYet_ShowsThePlaceholderAndNoMap()
    {
        var html = Render(new StatItem { Label = "Where", FieldKey = "location", Value = null, Display = "map" });

        Assert.DoesNotContain("data-wayfinder-location-map", html);
        Assert.Contains("""<div class="wayfinder-stat-card__value">—</div>""", html);
    }

    [Fact]
    public void AValueIsEscapedWhereItIsWrittenIntoTheMapAttribute()
    {
        var html = Render(new StatItem { Label = "Where", FieldKey = "location", Value = "1,2\" onmouseover=\"x", Display = "map" });

        Assert.DoesNotContain("onmouseover=\"x", html);
        Assert.Contains("&quot;", html);
    }

    [Fact]
    public void TheDisplayChoiceSurvivesSerialisation_AndIsOmittedWhenUnset()
    {
        var withMap = JsonSerializer.Serialize(new StatItemDefinition { Label = "Where", FieldKey = "location", Display = "map" });
        var without = JsonSerializer.Serialize(new StatItemDefinition { Label = "Where", FieldKey = "location" });

        Assert.Contains("\"Display\":\"map\"", withMap);
        Assert.DoesNotContain("Display", without);
    }

    [Fact]
    public void TheEngineCarriesDisplayAndTheCapturedPointToTheRenderedTile()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>("""
            {
              "definitionKey": "map-test", "displayName": "Map test", "version": 1, "initialStage": "where", "requestPolicy": "single",
              "queues": [{ "key": "citizen", "displayName": "Applicant", "actor": "citizen" }],
              "stages": [
                { "stageKey": "where", "displayName": "Where", "queueKey": "citizen",
                  "components": [{ "type": "location-picker", "fieldKey": "location", "label": "Where" }],
                  "routes": [{ "id": "where--continue", "target": "go", "trigger": "continue", "label": "Continue" }] },
                { "stageKey": "recorded", "displayName": "Recorded", "queueKey": "citizen",
                  "components": [{ "type": "stat-group", "items": [{ "label": "Where", "fieldKey": "location", "display": "map" }] }] }
              ],
              "gateways": [
                { "key": "go", "displayName": "Go", "gatewayType": "Split", "queueKey": "citizen",
                  "routes": [{ "id": "go--continue", "target": "recorded", "trigger": "continue" }] }
              ]
            }
            """, CaseInsensitive)!;
        var engine = new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition), new PassthroughContentSanitizer());

        var started = engine.GetCurrent("map-test", "tenant", "user");
        var recorded = engine.Advance(
            started.InstanceId, "tenant", "user", ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["location"] = "52.205300,0.121800" });

        Assert.Empty(recorded.Problems);
        var tile = recorded.Render!.Components.Single(c => c.Type == "stat-group").Stats!.Single();
        Assert.Equal("map", tile.Display);
        Assert.Equal("52.205300,0.121800", tile.Value);
    }
}
