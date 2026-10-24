using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Wayfinder.Serialization;

/// <summary>
/// Free-form values (an action's <c>params</c>) are plain .NET values: a mapping reads as
/// <c>Dictionary&lt;string, object?&gt;</c> and a sequence as <c>List&lt;object?&gt;</c>.
/// </summary>
internal sealed class UntypedContainerTypeResolver : INodeTypeResolver
{
    public bool Resolve(NodeEvent? nodeEvent, ref Type currentType)
    {
        if (currentType != typeof(object))
        {
            return false;
        }

        switch (nodeEvent)
        {
            case MappingStart:
                currentType = typeof(Dictionary<string, object?>);
                return true;
            case SequenceStart:
                currentType = typeof(List<object?>);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// A free-form scalar takes its YAML 1.2 core-schema type: an unquoted <c>true</c>, <c>3</c> or
/// <c>2.5</c> reads as a <see cref="bool"/>, <see cref="long"/> or <see cref="decimal"/>, an
/// unquoted <c>null</c> or <c>~</c> as null, and anything quoted stays a string.
/// </summary>
internal sealed class UntypedScalarNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        if (expectedType == typeof(object) && reader.TryConsume<Scalar>(out var scalar))
        {
            value = scalar.Style == ScalarStyle.Plain ? Resolve(scalar.Value) : scalar.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static object? Resolve(string text) => text switch
    {
        "" or "~" or "null" or "Null" or "NULL" => null,
        "true" or "True" or "TRUE" => true,
        "false" or "False" or "FALSE" => false,
        _ when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => whole,
        _ when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => text,
    };
}
