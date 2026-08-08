using System.Text.Json;
using System.Text.Json.Serialization;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// The single source of <see cref="JsonSerializerOptions"/> for reading/writing a
/// <see cref="ServiceBlueprint"/> (and therefore its polymorphic <c>Component</c> tree).
/// Every host-side store and MCP tool that (de)serializes a blueprint should use these
/// instances rather than constructing its own — before this existed, four call sites
/// (<c>FilesystemServiceBlueprintStore</c>, <c>FilesystemServiceBlueprintSourceStore</c>'s own
/// read and write options, and <c>ServiceBlueprintAuthoringTools</c>) each built a slightly
/// different, independently-drifting options instance; one of them (the boot-time seed loader)
/// was missing the out-of-order-metadata tolerance the other two already needed for real seed
/// files, a latent bug this consolidation fixes as a side effect rather than a special case.
/// Also the one place <see cref="ComponentTypeRegistry.CreateJsonTypeInfoResolver"/> is wired
/// in, so a component type registered at runtime (built-in or a toolkit extension's own) is
/// recognised everywhere a blueprint is read or written — not four places to keep in sync.
/// </summary>
public static class ServiceBlueprintJson
{
    /// <summary>
    /// For deserializing a <see cref="ServiceBlueprint"/> from disk, an MCP tool argument, or
    /// any other external source. <c>AllowOutOfOrderMetadataProperties</c> is required because a
    /// hand-authored or model-constructed component's <c>"type"</c> discriminator can't be
    /// relied on to be the first JSON property.
    /// </summary>
    public static JsonSerializerOptions ReadOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
        TypeInfoResolver = ComponentTypeRegistry.CreateJsonTypeInfoResolver(),
    };

    /// <summary>For serializing a <see cref="ServiceBlueprint"/> back out to storage.</summary>
    public static JsonSerializerOptions WriteOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        TypeInfoResolver = ComponentTypeRegistry.CreateJsonTypeInfoResolver(),
    };
}
