namespace Wayfinder.Engine.Services;

internal static class FieldValueMerge
{
    public static Dictionary<string, object?> Merge(
        Dictionary<string, object?> existing,
        Dictionary<string, object?>? incoming)
    {
        if (incoming == null || incoming.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, object?>(existing);
        foreach (var kvp in incoming)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }
}
