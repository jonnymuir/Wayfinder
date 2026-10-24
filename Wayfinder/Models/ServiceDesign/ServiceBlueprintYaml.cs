using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// The one way a <see cref="ServiceBlueprint"/> (or any part of one) is written down: YAML.
/// Every store, authoring tool and test reads and writes blueprints through here, so there is a
/// single definition of the shape: camelCase keys, a component's <c>type</c> discriminator first,
/// nulls omitted, multi-line text as literal blocks. Reading is strict: an unknown key or
/// component type is an error with its line and column, never silently dropped, and anchors and
/// aliases are refused (see <see cref="NoAliasParser"/>).
/// </summary>
public static class ServiceBlueprintYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(NullNamingConvention.Instance)
        .WithTypeInspector(inner => new BlueprintTypeInspector(inner))
        .WithTypeDiscriminatingNodeDeserializer(options => options.AddTypeDiscriminator(new ComponentTypeDiscriminator()))
        .WithNodeDeserializer(new ReadOnlyCollectionNodeDeserializer(), where => where.OnTop())
        .WithNodeDeserializer(new UntypedScalarNodeDeserializer(), where => where.OnTop())
        .WithNodeTypeResolver(new UntypedContainerTypeResolver(), where => where.OnTop())
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(NullNamingConvention.Instance)
        .WithTypeInspector(inner => new BlueprintTypeInspector(inner))
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithQuotingNecessaryStrings()
        .WithEventEmitter(next => new LiteralBlockEventEmitter(next))
        .WithIndentedSequences()
        .DisableAliases()
        .Build();

    /// <summary>Reads a whole blueprint.</summary>
    /// <exception cref="ServiceBlueprintYamlException">The text is not valid YAML or not a valid blueprint shape.</exception>
    public static ServiceBlueprint Deserialize(string yaml) => Deserialize<ServiceBlueprint>(yaml);

    /// <summary>Reads any part of a blueprint (a stage, a component list, a calculations block).</summary>
    /// <exception cref="ServiceBlueprintYamlException">The text is not valid YAML or not a valid shape for <typeparamref name="T"/>.</exception>
    public static T Deserialize<T>(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        try
        {
            return Deserializer.Deserialize<T>(new NoAliasParser(new Parser(new StringReader(yaml))))
                ?? throw new ServiceBlueprintYamlException("The document is empty.", line: 1, column: 1);
        }
        catch (YamlException ex)
        {
            throw ServiceBlueprintYamlException.From(ex);
        }
    }

    /// <summary>
    /// Reads a whole blueprint supplied by an author or an agent, turning unreadable text into an
    /// <c>INVALID_YAML</c> diagnostic (with the line and column) instead of a throw, so an authoring
    /// surface can report it alongside ordinary validation diagnostics.
    /// </summary>
    public static bool TryDeserialize(
        string yaml,
        [NotNullWhen(true)] out ServiceBlueprint? blueprint,
        [NotNullWhen(false)] out ServiceBlueprintDiagnostic? diagnostic)
    {
        try
        {
            blueprint = Deserialize(yaml);
            diagnostic = null;
            return true;
        }
        catch (ServiceBlueprintYamlException ex)
        {
            blueprint = null;
            diagnostic = new ServiceBlueprintDiagnostic("INVALID_YAML", "$", $"Not a valid service blueprint. {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes a blueprint, or any part of one, as its runtime type (so a <see cref="Component"/> keeps its <c>type</c>).</summary>
    public static string Serialize<T>(T value) where T : notnull => Serializer.Serialize(value);

    /// <summary>
    /// The properties a type is written with, by name, in the order they are written: simple values
    /// before nested lists and mappings, and a base type's properties before a derived type's, so a
    /// stage opens with its key and name and a component with its <c>fieldKey</c> and <c>label</c>.
    /// A component's <c>type</c> is not included; it is always written first. The editor's generated
    /// model carries the same order, so the Definition tab reads like the server's YAML.
    /// </summary>
    public static IReadOnlyList<(string Name, PropertyInfo Property)> WrittenProperties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var hierarchy = new List<Type>();
        for (var level = type; level is not null && level != typeof(object); level = level.BaseType)
        {
            hierarchy.Insert(0, level);
        }

        return hierarchy
            .SelectMany((level, depth) => level
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.GetIndexParameters().Length == 0 && property.GetCustomAttribute<YamlIgnoreAttribute>() is null)
                .Select((property, index) => (Property: property, Depth: depth, Index: index)))
            .OrderBy(entry => IsNested(entry.Property.PropertyType))
            .ThenBy(entry => entry.Depth)
            .ThenBy(entry => entry.Index)
            .Select(entry => (WrittenName(entry.Property), entry.Property))
            .ToArray();
    }

    private static string WrittenName(PropertyInfo property) =>
        property.GetCustomAttribute<YamlMemberAttribute>()?.Alias
            ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    private static bool IsNested(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return !(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid));
    }
}
