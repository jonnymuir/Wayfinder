using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Wayfinder.Serialization;

/// <summary>
/// The model exposes collections as <see cref="IReadOnlyList{T}"/> and
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, which YamlDotNet cannot instantiate; read them
/// as the matching <see cref="List{T}"/> or <see cref="Dictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class ReadOnlyCollectionNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        if (Concrete(expectedType) is { } concrete)
        {
            value = nestedObjectDeserializer(reader, concrete);
            return true;
        }

        value = null;
        return false;
    }

    private static Type? Concrete(Type type)
    {
        if (!type.IsInterface || !type.IsGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (definition == typeof(IReadOnlyDictionary<,>))
        {
            return typeof(Dictionary<,>).MakeGenericType(arguments);
        }

        return definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
            ? typeof(List<>).MakeGenericType(arguments)
            : null;
    }
}
