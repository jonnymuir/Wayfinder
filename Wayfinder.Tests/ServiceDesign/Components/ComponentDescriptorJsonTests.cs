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
}
