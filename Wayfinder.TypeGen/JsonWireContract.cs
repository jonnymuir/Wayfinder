using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Wayfinder.TypeGen;

/// <summary>
/// The shape System.Text.Json writes with <paramref name="options"/>, read from the serializer's own
/// metadata: property names, polymorphic discriminators, enum representation. Used for the
/// JSON results of the authoring API (catalogs, diagnostics, outcomes).
/// </summary>
public sealed class JsonWireContract(JsonSerializerOptions options) : WireContract
{
    private readonly NullabilityInfoContext _nullability = new();

    public override (WireKind Kind, Type? Element) Classify(Type type)
    {
        var info = options.GetTypeInfo(type);
        return info.Kind switch
        {
            JsonTypeInfoKind.Dictionary => (WireKind.Map, info.ElementType),
            JsonTypeInfoKind.Enumerable => (WireKind.List, info.ElementType),
            _ => (WireKind.Structure, null),
        };
    }

    public override IReadOnlyList<WireMember> Members(Type type) =>
        options.GetTypeInfo(type).Properties
            .Select(member => new WireMember(
                member.Name,
                member.PropertyType,
                Optional: !member.IsRequired && CanBeAbsent(member),
                TypeScript: ConverterOverride(member)))
            .ToArray();

    public override WirePolymorphism? Polymorphism(Type type) =>
        options.GetTypeInfo(type).PolymorphismOptions is { } polymorphism
            ? new WirePolymorphism(
                polymorphism.TypeDiscriminatorPropertyName,
                polymorphism.DerivedTypes.Select(derived => (derived.DerivedType, derived.TypeDiscriminator!.ToString()!)).ToArray())
            : null;

    public override string EnumValue(Type enumType, object value)
    {
        if (!IsStringEnumConverter(enumType.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType))
        {
            throw new InvalidOperationException($"{enumType.Name} is not serialized as a string; teach the emitter its representation.");
        }

        return JsonSerializer.Serialize(value, enumType, options).Trim('"');
    }

    private static bool IsStringEnumConverter(Type? converter)
    {
        for (var type = converter; type is not null; type = type.BaseType)
        {
            if (type == typeof(JsonStringEnumConverter)
                || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A property-level <c>[JsonConverter]</c> can change the wire shape. Converters over a string
    /// (or a <see cref="Type"/>, written as its name) are plain strings on the wire; any other has to be taught
    /// here, because guessing would let the TypeScript drift from the wire.
    /// </summary>
    private static string? ConverterOverride(JsonPropertyInfo member)
    {
        var converter = member.AttributeProvider?.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: true)
            .OfType<JsonConverterAttribute>().FirstOrDefault()?.ConverterType;
        if (converter is null)
        {
            return null;
        }

        for (var type = converter; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(JsonConverter<>)
                && type.GetGenericArguments()[0] is var written && (written == typeof(string) || written == typeof(Type)))
            {
                return "string";
            }
        }

        throw new InvalidOperationException(
            $"{member.Name} uses {converter.Name}; teach JsonWireContract.ConverterOverride the shape it writes.");
    }

    /// <summary>
    /// Whether a reader can see the property missing: its C# type is nullable, or the writer
    /// omits it when null/default (so a document that round-tripped through the server may lack it).
    /// </summary>
    private bool CanBeAbsent(JsonPropertyInfo member) =>
        IsNullable(member)
        || member.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .Any(ignore => ignore.Condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault) == true;

    private bool IsNullable(JsonPropertyInfo member)
    {
        if (Nullable.GetUnderlyingType(member.PropertyType) is not null)
        {
            return true;
        }

        return member.AttributeProvider is PropertyInfo property
            && _nullability.Create(property).ReadState == NullabilityState.Nullable;
    }
}
