namespace Wayfinder.Engine.Services.Validation;

/// <summary>Helpers for the dotted names (<c>member.address.postcode</c>) calculation expressions use to reach into an object-valued field.</summary>
internal static class DottedNames
{
    /// <summary>
    /// True if <paramref name="dottedName"/> itself, or any of its dot-separated prefixes (walked
    /// shortest to longest), is in <paramref name="names"/>. A gap can be tracked at either grain: a
    /// whole unresolved field ("member") or one specific unresolved property within an
    /// otherwise-resolved shape ("member.age") — a reference at or past either point is equally
    /// expected to fail.
    /// </summary>
    public static bool ContainsAnyPrefix(IReadOnlySet<string> names, string dottedName)
    {
        var prefix = string.Empty;
        foreach (var segment in dottedName.Split('.'))
        {
            prefix = prefix.Length == 0 ? segment : $"{prefix}.{segment}";
            if (names.Contains(prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites a dotted service-field path ("member.address.postcode") to where that property
    /// actually lives in the JSON ("member.shape.address.shape.postcode") — every segment past the
    /// first is nested one level deeper under its own "shape" key.
    /// </summary>
    public static string JsonShapePath(string dottedName) => string.Join(".shape.", dottedName.Split('.'));
}
