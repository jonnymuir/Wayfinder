using System.Text.Json;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign.Components;

/// <summary>
/// A real bug found only by actually curling the live `GET /component-types` endpoint (see
/// Wayfinder.Engine.Api's ServiceBlueprintAuthoringApiExtensions and Phase 6's
/// component-catalog.ts): ComponentCategory/ComponentPropertyValueKind/ContainmentKind had no
/// converter of their own (unlike ServiceBlueprintSaveStatus), and neither
/// ServiceBlueprintJson's shared options nor ASP.NET Core's own default JSON options apply a
/// global string-enum converter — so every consumer of this descriptor JSON (the editor client's
/// TS side included, which declares these fields as string unions) silently received a raw
/// integer instead. Locks the fix in against *any* JsonSerializerOptions, not just
/// ServiceBlueprintJson's — the bug was specifically that this shouldn't depend on which options
/// a caller happens to use.
/// </summary>
public class ComponentDescriptorJsonTests
{
    [Fact]
    public void Serialize_WithPlainWebDefaults_WritesEnumsAsStrings()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = ComponentTypeRegistry.All.Single(d => d.Discriminator == "accordion");

        var json = JsonSerializer.Serialize(descriptor, options);

        json.Should().Contain("\"category\":\"Container\"");
        json.Should().Contain("\"kind\":\"NamedSections\"");
        json.Should().NotMatchRegex("\"category\":\\d");
        json.Should().NotMatchRegex("\"kind\":\\d");
    }

    [Fact]
    public void Serialize_PropertyValueKind_WritesAsString()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = ComponentTypeRegistry.All.Single(d => d.Discriminator == "text");

        var json = JsonSerializer.Serialize(descriptor, options);

        json.Should().Contain("\"valueKind\":\"String\"");
        json.Should().NotMatchRegex("\"valueKind\":\\d");
    }

    [Fact]
    public void Serialize_ClrType_WritesJustTheTypeName_UnderTheClrTypeKey()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = ComponentTypeRegistry.All.Single(d => d.Discriminator == "accordion");

        var json = JsonSerializer.Serialize(descriptor, options);

        json.Should().Contain("\"clrType\":\"AccordionComponent\"");
    }

    /// <summary>
    /// A second real bug found only by driving the properties-panel editor UI (phase 6) against
    /// a live host, not by any test: <see cref="ComponentPropertyDescriptor.Key"/> is the raw C#
    /// CLR property name (e.g. "FieldKey", set via <see langword="nameof"/> in
    /// BuiltInComponentDescriptors.cs, deliberately, for compile-time rename-safety and because
    /// ComponentPropertyValidator reflects against it at runtime). The editor client read/wrote
    /// that value directly against real component JSON, which uses the camelCase wire property
    /// ("fieldKey") — every field in the add/edit form appeared blank, and an edit never reached
    /// the property the runtime actually reads. Fixed with <see cref="PropertyNameJsonConverter"/>,
    /// converting once at the JSON boundary rather than asking every client call site to
    /// remember to camelCase it itself.
    /// </summary>
    [Fact]
    public void Serialize_PropertyDescriptorKey_WritesAsCamelCase_NotTheRawClrPropertyName()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var descriptor = ComponentTypeRegistry.All.Single(d => d.Discriminator == "email");

        var json = JsonSerializer.Serialize(descriptor, options);

        json.Should().Contain("\"key\":\"fieldKey\"");
        json.Should().Contain("\"key\":\"label\"");
        json.Should().NotContain("\"key\":\"FieldKey\"");
        json.Should().NotContain("\"key\":\"Label\"");
    }

    /// <summary>
    /// Same conversion, same reason, for <see cref="ComponentContainment"/>'s own property-name
    /// fields — a container's <c>propertyName</c>/<c>sectionChildrenPropertyName</c>/
    /// <c>keySourceProperty</c> address real component JSON exactly like
    /// <see cref="ComponentPropertyDescriptor.Key"/> does, and hit the identical bug.
    /// </summary>
    [Fact]
    public void Serialize_ContainmentPropertyNames_WriteAsCamelCase()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var fieldset = ComponentTypeRegistry.All.Single(d => d.Discriminator == "fieldset");
        var radio = ComponentTypeRegistry.All.Single(d => d.Discriminator == "radio");

        var fieldsetJson = JsonSerializer.Serialize(fieldset, options);
        fieldsetJson.Should().Contain("\"propertyName\":\"children\"");
        fieldsetJson.Should().NotContain("\"propertyName\":\"Children\"");

        var radioJson = JsonSerializer.Serialize(radio, options);
        radioJson.Should().Contain("\"propertyName\":\"conditionalChildren\"");
        radioJson.Should().Contain("\"keySourceProperty\":\"options\"");
    }
}
