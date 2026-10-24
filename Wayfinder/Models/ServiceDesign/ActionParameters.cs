namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Typed reads of an action's free-form <see cref="ActionDefinition.Parameters"/>. Values are
/// plain .NET values as YAML gives them: strings, <see cref="bool"/>, <see cref="long"/> or
/// <see cref="decimal"/> numbers, nested <c>IReadOnlyDictionary&lt;string, object?&gt;</c> maps
/// and <c>IReadOnlyList&lt;object?&gt;</c> lists. A read of the wrong shape is null, never a throw.
/// </summary>
public static class ActionParameters
{
    public static string? GetString(this IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.GetValueOrDefault(key) as string;

    public static IReadOnlyDictionary<string, object?>? GetMap(this IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.GetValueOrDefault(key) as IReadOnlyDictionary<string, object?>;

    public static IReadOnlyList<object?>? GetList(this IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.GetValueOrDefault(key) as IReadOnlyList<object?>;

    public static bool? GetBool(this IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.GetValueOrDefault(key) as bool?;

    /// <summary>A whole number, or null when absent, fractional, out of <see cref="int"/> range, or not a number.</summary>
    public static int? GetInt(this IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.GetValueOrDefault(key) switch
        {
            int whole => whole,
            long whole when whole is >= int.MinValue and <= int.MaxValue => (int)whole,
            decimal number when number == decimal.Truncate(number) && number is >= int.MinValue and <= int.MaxValue => (int)number,
            _ => null,
        };
}
