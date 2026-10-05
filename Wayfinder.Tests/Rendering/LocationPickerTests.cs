using System.Text.Json;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Rendering.GovUk;
using Wayfinder.Services.Validation;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// The <c>location-picker</c> stores one point as <c>latitude,longitude</c> in WGS84 decimal
/// degrees, checked server-side, and renders a plain text input that works with no script.
/// </summary>
public class LocationPickerTests
{
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    private static FieldRenderPayload Field(bool required = false, string? value = null) => new()
    {
        FieldKey = "location",
        Label = "Where did you see it?",
        FieldType = "location-picker",
        Required = required,
        Value = value,
    };

    private static FieldValidationResult Validate(string? submitted, bool required = false)
    {
        var values = submitted is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["location"] = submitted };
        return FieldValueValidator.Validate([Field(required)], values);
    }

    [Theory]
    [InlineData("51.5074,-0.1278", 51.5074, -0.1278)]
    [InlineData("51.5074, -0.1278", 51.5074, -0.1278)]
    [InlineData(" 90 , 180 ", 90, 180)]
    [InlineData("-90,-180", -90, -180)]
    [InlineData("0,0", 0, 0)]
    public void AValidPoint_ParsesToItsCoordinates(string value, double latitude, double longitude)
    {
        LocationValue.TryParse(value, out var lat, out var lng).Should().BeTrue();
        lat.Should().Be(latitude);
        lng.Should().Be(longitude);
    }

    [Theory]
    [InlineData("")]
    [InlineData("51.5074")]
    [InlineData("51.5074,-0.1278,10")]
    [InlineData("north,west")]
    [InlineData("90.0001,0")]
    [InlineData("-90.0001,0")]
    [InlineData("0,180.0001")]
    [InlineData("0,-180.0001")]
    [InlineData("NaN,0")]
    [InlineData("0,Infinity")]
    [InlineData("1e2,0")]
    [InlineData("51,5074,-0,1278")]
    public void ANonPointOrOutOfRangePoint_DoesNotParse(string value)
    {
        LocationValue.TryParse(value, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ANullValue_DoesNotParse()
    {
        LocationValue.TryParse(null, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TheFormValidator_AcceptsAValidPoint()
    {
        Validate("51.5074,-0.1278").IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("somewhere near the hedge")]
    [InlineData("95,0")]
    [InlineData("0,200")]
    public void TheFormValidator_RejectsAValueThatIsNotAPointAndNamesTheField(string value)
    {
        var result = Validate(value);

        result.IsValid.Should().BeFalse();
        result.Errors["location"].Should().StartWith("Where did you see it?");
    }

    [Fact]
    public void ARequiredPicker_RejectsAnEmptyValue()
    {
        Validate("", required: true).Errors["location"].Should().Be("Where did you see it? is required.");
    }

    [Fact]
    public void AnOptionalPicker_AcceptsNoValue()
    {
        Validate(null).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ThePickerRenders_AWorkingTextInputWithItsValueAndAHint()
    {
        var html = GovUkFields.Render(Field(required: true, value: "51.5074,-0.1278"), NoErrors);

        html.Should().Contain($"name=\"{GovUk.FieldName("location")}\"").And.Contain("value=\"51.5074,-0.1278\"")
            .And.Contain("type=\"text\"").And.Contain("required").And.Contain("for=\"location\"")
            .And.Contain("for example 51.5074, -0.1278");
    }

    [Fact]
    public void ThePickerRenders_AnErrorMessageLinkedToTheInput()
    {
        var html = GovUkFields.Render(Field(), new Dictionary<string, string> { ["location"] = "Enter a valid point" });

        html.Should().Contain("Enter a valid point").And.Contain("govuk-input--error")
            .And.Contain("aria-describedby=\"location-hint location-error\"");
    }

    [Fact]
    public void ThePickerEscapesAHostileValue()
    {
        var html = GovUkFields.Render(Field(value: "\"><script>alert(1)</script>"), NoErrors);

        html.Should().NotContain("<script>alert(1)</script>");
    }

    [Fact]
    public void ABlueprintComponentOfThisTypeRoundTripsThroughJson()
    {
        const string json = """{"type":"location-picker","fieldKey":"location","label":"Where?","required":true}""";

        var component = JsonSerializer.Deserialize<Component>(json, ServiceBlueprintJson.ReadOptions);

        component.Should().BeOfType<LocationPickerComponent>()
            .Which.Should().Match<LocationPickerComponent>(c => c.FieldKey == "location" && c.Required);
        JsonSerializer.Serialize(component, ServiceBlueprintJson.WriteOptions).Should().Contain("\"type\": \"location-picker\"");
    }
}
