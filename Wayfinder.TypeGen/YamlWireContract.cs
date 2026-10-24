using System.Reflection;
using System.Runtime.Serialization;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using YamlDotNet.Serialization;

namespace Wayfinder.TypeGen;

/// <summary>
/// The shape <see cref="ServiceBlueprintYaml"/> writes a blueprint in: the properties and order of
/// <see cref="ServiceBlueprintYaml.WrittenProperties"/>; enums by name or
/// <see cref="EnumMemberAttribute.Value"/>; a component as one of the
/// <see cref="ComponentTypeRegistry"/>'s types, discriminated by <c>type</c>. Nulls are omitted, so
/// a nullable property is optional, as is one that omits its default or empty value.
/// </summary>
public sealed class YamlWireContract : WireContract
{
    private readonly NullabilityInfoContext _nullability = new();

    public override (WireKind Kind, Type? Element) Classify(Type type)
    {
        if (GenericArguments(type, typeof(IReadOnlyDictionary<,>)) is [_, var value])
        {
            return (WireKind.Map, value);
        }

        return GenericArguments(type, typeof(IEnumerable<>)) is [var element] ? (WireKind.List, element) : (WireKind.Structure, null);
    }

    public override bool OrdersKeys => true;

    public override IReadOnlyList<WireMember> Members(Type type) =>
        ServiceBlueprintYaml.WrittenProperties(type)
            .Select(written => new WireMember(
                written.Name,
                written.Property.PropertyType,
                Optional: IsNullable(written.Property) || OmitsDefault(written.Property.GetCustomAttribute<YamlMemberAttribute>())))
            .ToArray();

    public override WirePolymorphism? Polymorphism(Type type) =>
        type == typeof(Component)
            ? new WirePolymorphism("type", ComponentTypeRegistry.All.Select(descriptor => (descriptor.ClrType, descriptor.Discriminator)).ToArray())
            : null;

    public override string EnumValue(Type enumType, object value)
    {
        var name = Enum.GetName(enumType, value)!;
        return enumType.GetField(name)!.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? name;
    }

    private static Type[]? GenericArguments(Type type, Type definition)
    {
        if (type == typeof(string))
        {
            return null;
        }

        return (type.IsGenericType && type.GetGenericTypeDefinition() == definition ? type : null)?.GetGenericArguments()
            ?? type.GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == definition)?.GetGenericArguments();
    }

    private static bool OmitsDefault(YamlMemberAttribute? member) =>
        member is not null
        && (member.DefaultValuesHandling & (DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections)) != 0;

    private bool IsNullable(PropertyInfo property) =>
        Nullable.GetUnderlyingType(property.PropertyType) is not null
        || _nullability.Create(property).ReadState == NullabilityState.Nullable;
}
