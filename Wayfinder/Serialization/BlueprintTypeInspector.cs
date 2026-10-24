using System.Collections.Concurrent;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.TypeInspectors;

namespace Wayfinder.Serialization;

/// <summary>
/// Shapes how a blueprint's types read as YAML:
/// <list type="bullet">
/// <item>every registered component type gets a <c>type</c> property, written first and holding
/// its discriminator; on reading it is accepted and ignored, since
/// <see cref="ComponentTypeDiscriminator"/> already used it to pick the type;</item>
/// <item>properties are written in <see cref="ServiceBlueprintYaml.WrittenProperties"/>'s order.</item>
/// </list>
/// </summary>
internal sealed class BlueprintTypeInspector(ITypeInspector innerTypeDescriptor) : ReflectionTypeInspector
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, int>> Ranks = new();

    public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
    {
        var ranks = Ranks.GetOrAdd(type, Rank);
        var properties = innerTypeDescriptor.GetProperties(type, container)
            .OrderBy(property => ranks.TryGetValue(property.Name, out var rank) ? rank : int.MaxValue);
        return typeof(Component).IsAssignableFrom(type) && ComponentTypeRegistry.FindByClrType(type) is { } descriptor
            ? properties.Prepend(new DiscriminatorProperty(descriptor.Discriminator))
            : properties;
    }

    public override string GetEnumName(Type enumType, string name) => innerTypeDescriptor.GetEnumName(enumType, name);

    public override string GetEnumValue(object enumValue) => innerTypeDescriptor.GetEnumValue(enumValue);

    private static Dictionary<string, int> Rank(Type type) =>
        ServiceBlueprintYaml.WrittenProperties(type)
            .Select((property, index) => (property.Name, index))
            .ToDictionary(entry => entry.Name, entry => entry.index, StringComparer.Ordinal);

    private sealed class DiscriminatorProperty(string discriminator) : IPropertyDescriptor
    {
        public string Name => "type";
        public bool AllowNulls => false;
        public bool CanWrite => true;
        public Type Type => typeof(string);
        public Type? TypeOverride { get; set; }
        public int Order { get; set; } = int.MinValue;
        public ScalarStyle ScalarStyle { get; set; }
        public bool Required => false;
        public Type? ConverterType => null;

        public T? GetCustomAttribute<T>() where T : Attribute => null;

        public IObjectDescriptor Read(object target) => new ObjectDescriptor(discriminator, typeof(string), typeof(string));

        public void Write(object target, object? value)
        {
        }
    }
}
