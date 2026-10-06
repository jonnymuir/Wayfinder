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
/// A date input can start on today, and a text input on the current time, taken from the visitor's own
/// device clock (the server cannot know their local day or time). The markup only says what to do; a script
/// fills it in, and a field that already has a value is left alone (see the real-browser test).
/// </summary>
public class DeviceClockDefaultTests
{
    private static readonly GovUkComponentRenderer Renderer = new();
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string Render(FieldRenderPayload field)
    {
        var step = new StepContent
        {
            StepType = "question",
            StateDisplayName = "Test",
            Components = [new ComponentRenderPayload { Type = "fieldset", Fields = [field] }],
            AvailableActions = [],
        };
        return Renderer.RenderForm(step, [], "/test", 0);
    }

    [Fact]
    public void ADateThatDefaultsToToday_MarksItsDateGroup()
    {
        var html = Render(new FieldRenderPayload { FieldKey = "d", Label = "Date", FieldType = "date", Required = false, DeviceDefault = "today" });

        Assert.Contains("""<div class="govuk-date-input" id="d" data-wayfinder-device-default="today">""", html);
    }

    [Fact]
    public void ATextThatDefaultsToTheTime_MarksItsInput()
    {
        var html = Render(new FieldRenderPayload { FieldKey = "t", Label = "Time", FieldType = "text", Required = false, DeviceDefault = "time" });

        Assert.Contains("""data-wayfinder-device-default="time">""", html);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("text")]
    public void AFieldWithNoDeviceDefault_CarriesNoMarker(string fieldType)
    {
        var html = Render(new FieldRenderPayload { FieldKey = "f", Label = "Field", FieldType = fieldType, Required = false });

        Assert.DoesNotContain("data-wayfinder-device-default", html);
    }

    [Fact]
    public void TheChoicesAreOmittedFromAuthoredJsonWhenOff_AndKeptWhenOn()
    {
        Assert.DoesNotContain("DefaultToToday", JsonSerializer.Serialize(new DateInputComponent { FieldKey = "d" }));
        Assert.DoesNotContain("DefaultToCurrentTime", JsonSerializer.Serialize(new TextInputComponent { FieldKey = "t" }));
        Assert.Contains("\"DefaultToToday\":true", JsonSerializer.Serialize(new DateInputComponent { FieldKey = "d", DefaultToToday = true }));
        Assert.Contains("\"DefaultToCurrentTime\":true", JsonSerializer.Serialize(new TextInputComponent { FieldKey = "t", DefaultToCurrentTime = true }));
    }

    [Fact]
    public void TheEngineMarksOnlyTheFieldsThatAskedForADeviceDefault()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>("""
            {
              "definitionKey": "clock-test", "displayName": "Clock test", "version": 1, "initialStage": "when", "requestPolicy": "single",
              "queues": [{ "key": "citizen", "displayName": "Applicant", "actor": "citizen" }],
              "stages": [
                { "stageKey": "when", "displayName": "When", "queueKey": "citizen",
                  "components": [{ "type": "fieldset", "legend": "When", "children": [
                    { "type": "date", "fieldKey": "day", "label": "Day", "defaultToToday": true },
                    { "type": "text", "fieldKey": "time", "label": "Time", "defaultToCurrentTime": true },
                    { "type": "text", "fieldKey": "plain", "label": "Plain" } ] }],
                  "routes": [] }
              ],
              "gateways": []
            }
            """, CaseInsensitive)!;
        var engine = new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition), new PassthroughContentSanitizer());

        var fields = engine.GetCurrent("clock-test", "tenant", "user").Render!.Components.Single().Fields.ToDictionary(f => f.FieldKey);

        Assert.Equal("today", fields["day"].DeviceDefault);
        Assert.Equal("time", fields["time"].DeviceDefault);
        Assert.Null(fields["plain"].DeviceDefault);
    }
}
