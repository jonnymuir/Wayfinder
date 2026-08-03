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

    public static (string Day, string Month, string Year) SplitIsoDate(string? isoValue)
    {
        if (DateOnly.TryParse(isoValue, out var date))
        {
            return (date.Day.ToString(), date.Month.ToString(), date.Year.ToString());
        }

        return ("", "", "");
    }

    /// <summary>Combines posted day/month/year parts back into a single ISO ("yyyy-MM-dd") field value.</summary>
    public static string? CombineIsoDate(string? day, string? month, string? year)
    {
        if (int.TryParse(day, out var d) && int.TryParse(month, out var m) && int.TryParse(year, out var y))
        {
            try
            {
                return new DateOnly(y, m, d).ToString("yyyy-MM-dd");
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}
