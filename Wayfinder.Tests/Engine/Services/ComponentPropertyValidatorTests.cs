using FluentAssertions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

public class ComponentPropertyValidatorTests
{
    private static ComponentDescriptor RealDescriptorFor(string discriminator) =>
        ComponentTypeRegistry.All.Single(d => d.Discriminator == discriminator);

    [Fact]
    public void Validate_RequiredPropertyEmpty_ReturnsRequiredDiagnostic()
    {
        // FieldKey/Label default to "" rather than being C#-`required` — a genuinely empty
        // value round-trips through JSON as "", not null, so only the descriptor-driven check
        // (not the C# type system) catches this.
        var component = new TextInputComponent { FieldKey = "", Label = "" };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("text"), "stages.s.components[0]").ToList();

        diagnostics.Should().Contain(d => d.Code == "COMPONENT_PROPERTY_REQUIRED" && d.Path == "stages.s.components[0].fieldKey");
        diagnostics.Should().Contain(d => d.Code == "COMPONENT_PROPERTY_REQUIRED" && d.Path == "stages.s.components[0].label");
    }

    [Fact]
    public void Validate_AllRequiredPropertiesPresent_ReturnsNoDiagnostics()
    {
        var component = new TextInputComponent { FieldKey = "name", Label = "Name" };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("text"), "stages.s.components[0]").ToList();

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ChartKindNotAnAllowedValue_ReturnsInvalidValueDiagnostic()
    {
        var component = new ChartComponent
        {
            Series = "rows", X = "year", Kind = "pie-chart",
            Bands = [new ChartBand { Key = "a", Label = "A" }],
        };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("chart"), "stages.s.components[0]").ToList();

        diagnostics.Should().Contain(d =>
            d.Code == "COMPONENT_PROPERTY_INVALID_VALUE" && d.Path == "stages.s.components[0].kind");
    }

    [Fact]
    public void Validate_NestedArrayItemMissingRequiredFields_RecursesAndReportsAtItemPath()
    {
        var component = new ChartComponent
        {
            Series = "rows", X = "year",
            Bands = [new ChartBand { Key = "", Label = "" }],
        };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("chart"), "stages.s.components[0]").ToList();

        diagnostics.Should().Contain(d =>
            d.Code == "COMPONENT_PROPERTY_REQUIRED" && d.Path == "stages.s.components[0].bands[0].key");
        diagnostics.Should().Contain(d =>
            d.Code == "COMPONENT_PROPERTY_REQUIRED" && d.Path == "stages.s.components[0].bands[0].label");
    }

    [Fact]
    public void Validate_NumericPropertyAboveMaximum_ReturnsTooLargeDiagnostic()
    {
        var component = new HeadingComponent { Level = 9, Content = "Section" };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("heading"), "stages.s.components[0]").ToList();

        diagnostics.Should().ContainSingle(d =>
            d.Code == "COMPONENT_PROPERTY_TOO_LARGE" && d.Path == "stages.s.components[0].level");
    }

    [Fact]
    public void Validate_NumericPropertyWithinBounds_ReturnsNoNumericDiagnostic()
    {
        var component = new HeadingComponent { Level = 3, Content = "Section" };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("heading"), "stages.s.components[0]").ToList();

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Validate_KeyedChildrenKeyNotInOptions_ReturnsConditionalChildKeyMismatch()
    {
        var component = new RadiosComponent
        {
            FieldKey = "choice", Label = "Choice", Options = ["Yes", "No"],
            ConditionalChildren = new Dictionary<string, IReadOnlyList<Component>>
            {
                ["Maybe"] = [new TextInputComponent { FieldKey = "why", Label = "Why?" }],
            },
        };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("radio"), "stages.s.components[0]").ToList();

        diagnostics.Should().ContainSingle(d =>
            d.Code == "COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH" &&
            d.Path == "stages.s.components[0].conditionalChildren.Maybe");
    }

    [Fact]
    public void Validate_KeyedChildrenKeyMatchesOption_ReturnsNoConditionalChildKeyMismatch()
    {
        var component = new RadiosComponent
        {
            FieldKey = "choice", Label = "Choice", Options = ["Yes", "No"],
            ConditionalChildren = new Dictionary<string, IReadOnlyList<Component>>
            {
                ["Yes"] = [new TextInputComponent { FieldKey = "why", Label = "Why?" }],
            },
        };

        var diagnostics = ComponentPropertyValidator.Validate(component, RealDescriptorFor("radio"), "stages.s.components[0]").ToList();

        diagnostics.Should().NotContain(d => d.Code == "COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH");
    }

    // A synthetic descriptor exercises Pattern/MinLength/MaxLength/AllowedValues constraint
    // mechanics directly, decoupled from whichever constraints BuiltInComponentDescriptors
    // happens to declare for real types today (none currently use Pattern, for example).
    private static ComponentDescriptor SyntheticTextDescriptor(ComponentPropertyDescriptor fieldKeyProperty) => new()
    {
        Discriminator = "fixture-text", DisplayName = "Fixture text", Category = ComponentCategory.Input,
        ClrType = typeof(TextInputComponent),
        Properties = [fieldKeyProperty],
    };

    [Fact]
    public void Validate_StringPatternMismatch_ReturnsPatternDiagnostic()
    {
        var property = new ComponentPropertyDescriptor
        {
            Key = nameof(TextInputComponent.FieldKey), Title = "Field key",
            ValueKind = ComponentPropertyValueKind.String, Pattern = "^[a-z]+$",
        };
        var component = new TextInputComponent { FieldKey = "Not_Lowercase", Label = "L" };

        var diagnostics = ComponentPropertyValidator.Validate(component, SyntheticTextDescriptor(property), "$").ToList();

        diagnostics.Should().ContainSingle(d => d.Code == "COMPONENT_PROPERTY_PATTERN_MISMATCH");
    }

    [Fact]
    public void Validate_StringTooShort_ReturnsTooShortDiagnostic()
    {
        var property = new ComponentPropertyDescriptor
        {
            Key = nameof(TextInputComponent.FieldKey), Title = "Field key",
            ValueKind = ComponentPropertyValueKind.String, MinLength = 5,
        };
        var component = new TextInputComponent { FieldKey = "ab", Label = "L" };

        var diagnostics = ComponentPropertyValidator.Validate(component, SyntheticTextDescriptor(property), "$").ToList();

        diagnostics.Should().ContainSingle(d => d.Code == "COMPONENT_PROPERTY_TOO_SHORT");
    }

    [Fact]
    public void Validate_StringTooLong_ReturnsTooLongDiagnostic()
    {
        var property = new ComponentPropertyDescriptor
        {
            Key = nameof(TextInputComponent.FieldKey), Title = "Field key",
            ValueKind = ComponentPropertyValueKind.String, MaxLength = 2,
        };
        var component = new TextInputComponent { FieldKey = "abcdef", Label = "L" };

        var diagnostics = ComponentPropertyValidator.Validate(component, SyntheticTextDescriptor(property), "$").ToList();

        diagnostics.Should().ContainSingle(d => d.Code == "COMPONENT_PROPERTY_TOO_LONG");
    }
}
