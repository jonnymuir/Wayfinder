using System.Net;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// Small shared helpers used throughout this package's component/field render functions.
/// </summary>
public static class GovUk
{
    public static string Esc(string? value) => WebUtility.HtmlEncode(value ?? "");

    /// <summary>The <c>name</c> attribute a rendered form field posts under — <c>field:{fieldKey}</c>
    /// throughout this package, matching the convention its host-side field coercion expects.</summary>
    public static string FieldName(string fieldKey) => $"field:{fieldKey}";

    private const string RawDatePrefix = "raw:";

    public static (string Day, string Month, string Year) SplitIsoDate(string? isoValue)
    {
        if (DateOnly.TryParse(isoValue, out var date))
        {
            return (date.Day.ToString(), date.Month.ToString(), date.Year.ToString());
        }

        // Not a real calendar date, but CombineIsoDate below still preserves whatever day/month/
        // year the user actually typed (e.g. 31 February) — unpack that so a failed-validation
        // round trip shows the user's own input back, not a blank date field.
        if (isoValue is not null && isoValue.StartsWith(RawDatePrefix, StringComparison.Ordinal))
        {
            var parts = isoValue[RawDatePrefix.Length..].Split(':');
            if (parts.Length == 3)
            {
                return (parts[0], parts[1], parts[2]);
            }
        }

        return ("", "", "");
    }

    /// <summary>Combines posted day/month/year parts back into a single ISO ("yyyy-MM-dd") field value.</summary>
    public static string? CombineIsoDate(string? day, string? month, string? year)
    {
        if (string.IsNullOrWhiteSpace(day) && string.IsNullOrWhiteSpace(month) && string.IsNullOrWhiteSpace(year))
        {
            return null;
        }

        if (int.TryParse(day, out var d) && int.TryParse(month, out var m) && int.TryParse(year, out var y))
        {
            try
            {
                return new DateOnly(y, m, d).ToString("yyyy-MM-dd");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Not a real calendar date (e.g. 31 February) — fall through and preserve it
                // verbatim below, rather than discarding what the user typed.
            }
        }

        // Doesn't parse as a real date, but the user typed something in at least one box —
        // keep it (see SplitIsoDate's matching fallback) so a validation-error re-render can
        // show it back for correction instead of silently reverting to a blank field.
        return $"{RawDatePrefix}{day}:{month}:{year}";
    }
}
