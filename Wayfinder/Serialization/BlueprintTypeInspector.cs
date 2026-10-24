using System.Collections.Concurrent;
using System.Reflection;
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
/// <item>simple values come before nested lists and mappings, and a base type's properties before
/// a derived type's, so a stage opens with its key and name and a component with its
/// <c>fieldKey</c> and <c>label</c>, with nested content (components, routes, children) after.</item>
/// </list>
/// </summary>
internal sealed class BlueprintTypeInspector(ITypeInspector innerTypeDescriptor) : ReflectionTypeInspector
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, (bool Nested, int Depth, int Index)>> Ranks = new();

    public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
    {
        var ranks = Ranks.GetOrAdd(type, Rank);
        var properties = innerTypeDescriptor.GetProperties(type, container)
            .OrderBy(property => ranks.TryGetValue(property.Name, out var rank) ? rank : (true, int.MaxValue, int.MaxValue));
        return typeof(Component).IsAssignableFrom(type) && ComponentTypeRegistry.FindByClrType(type) is { } descriptor
            ? properties.Prepend(new DiscriminatorProperty(descriptor.Discriminator))
            : properties;
    }

    public override string GetEnumName(Type enumType, string name) => innerTypeDescriptor.GetEnumName(enumType, name);

    public override string GetEnumValue(object enumValue) => innerTypeDescriptor.GetEnumValue(enumValue);

    private static Dictionary<string, (bool Nested, int Depth, int Index)> Rank(Type type)
    {
        var hierarchy = new List<Type>();
        for (var level = type; level is not null && level != typeof(object); level = level.BaseType)
        {
            hierarchy.Insert(0, level);
        }

        var ranks = new Dictionary<string, (bool Nested, int Depth, int Index)>(StringComparer.Ordinal);
        for (var depth = 0; depth < hierarchy.Count; depth++)
        {
            var declared = hierarchy[depth].GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (var index = 0; index < declared.Length; index++)
            {
                var property = declared[index];
                var name = property.GetCustomAttribute<YamlMemberAttribute>()?.Alias
                    ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                ranks[name] = (IsNested(property.PropertyType), depth, index);
            }
        }

        return ranks;
    }

    private static bool IsNested(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return !(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid));
    }

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
