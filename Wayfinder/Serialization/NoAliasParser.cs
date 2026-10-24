using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Wayfinder.Serialization;

/// <summary>
/// Refuses YAML anchors and aliases. A blueprint never needs them, and allowing them lets a
/// small document (an agent's or an untrusted caller's) expand exponentially when it is next
/// written out, a "billion laughs" denial of service.
/// </summary>
internal sealed class NoAliasParser(IParser inner) : IParser
{
    public ParsingEvent? Current => inner.Current;

    public bool MoveNext()
    {
        if (!inner.MoveNext())
        {
            return false;
        }

        var current = inner.Current;
        if (current is AnchorAlias || current is NodeEvent { Anchor.IsEmpty: false })
        {
            throw new YamlException(current.Start, current.End, "Anchors and aliases (& and *) are not allowed in a service blueprint.");
        }

        return true;
    }
}
