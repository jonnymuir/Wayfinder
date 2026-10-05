using System.Globalization;

namespace Wayfinder.Services.Validation;

/// <summary>
/// The value a <c>location-picker</c> field stores: <c>latitude,longitude</c> in WGS84 decimal
/// degrees. Whitespace around either number is tolerated on input; consumers split on the comma.
/// </summary>
public static class LocationValue
{
    private const NumberStyles Numeric = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>
    /// Parses a <c>latitude,longitude</c> value. False for anything that is not exactly two finite
    /// numbers with latitude in [-90, 90] and longitude in [-180, 180].
    /// </summary>
    public static bool TryParse(string? value, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        var parts = value?.Split(',');
        return parts is { Length: 2 }
            && TryParseWithin(parts[0], 90, out latitude)
            && TryParseWithin(parts[1], 180, out longitude);
    }

    private static bool TryParseWithin(string text, double limit, out double number) =>
        double.TryParse(text.Trim(), Numeric, CultureInfo.InvariantCulture, out number)
        && double.IsFinite(number)
        && Math.Abs(number) <= limit;
}
