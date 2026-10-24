using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.BufferedDeserialization.TypeDiscriminators;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Serialization;

/// <summary>
/// Picks a component's CLR type from its <c>type</c> key, looked up in
/// <see cref="ComponentTypeRegistry"/> so a host's own registered types read like the built-ins.
/// </summary>
internal sealed class ComponentTypeDiscriminator : ITypeDiscriminator
{
    public Type BaseType => typeof(Component);

    public bool TryDiscriminate(IParser buffer, out Type? suggestedType)
    {
        var start = buffer.Current?.Start ?? Mark.Empty;
        if (!buffer.TryFindMappingEntry(key => key.Value == "type", out _, out var value) || value is not Scalar scalar)
        {
            throw new YamlException(start, start,
                $"A component needs a 'type', one of: {string.Join(", ", ComponentTypeRegistry.AllDiscriminators)}.");
        }

        suggestedType = ComponentTypeRegistry.Find(scalar.Value)?.ClrType
            ?? throw new YamlException(scalar.Start, scalar.End,
                $"'{scalar.Value}' is not a component type. Use one of: {string.Join(", ", ComponentTypeRegistry.AllDiscriminators)}.");
        return true;
    }
}
