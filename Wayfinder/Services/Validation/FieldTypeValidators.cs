using System.Globalization;
using System.Net.Mail;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Services.Validation;

/// <summary>
/// The per-type value checks <see cref="FieldValueValidator"/> dispatches to. Each returns the
/// field's error message, or null when the value is acceptable.
/// </summary>
internal static class FieldTypeValidators
{
    internal static string? Number(FieldRenderPayload field, string raw) =>
        decimal.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            ? null
            : $"{field.Label} must be a number.";

    internal static string? Location(FieldRenderPayload field, string raw) =>
        LocationValue.TryParse(raw, out _, out _)
            ? null
            : $"{field.Label} must be a latitude between -90 and 90 and a longitude between -180 and 180, separated by a comma, for example 51.5074, -0.1278.";

    internal static string? Email(FieldRenderPayload field, string raw)
    {
        try
        {
            return new MailAddress(raw).Address == raw ? null : $"{field.Label} must be a valid email address.";
        }
        catch (FormatException)
        {
            return $"{field.Label} must be a valid email address.";
        }
    }

    /// <summary>
    /// GDS multi-input date: <c>GetSubmittedValue</c> reconstructs as YYYY-MM-DD, or returns
    /// "PARTIAL" when only some sub-inputs are filled. The year must be 1900 to 2100 inclusive.
    /// </summary>
    internal static string? Date(FieldRenderPayload field, string raw)
    {
        if (raw == "PARTIAL")
        {
            return $"{field.Label} must include day, month, and year.";
        }

        if (!DateTime.TryParse(raw, out var parsedDate))
        {
            return $"{field.Label} must be a valid date.";
        }

        return parsedDate.Year is < 1900 or > 2100 ? $"{field.Label} year must be between 1900 and 2100." : null;
    }

    internal static string? DateAndTime(FieldRenderPayload field, string raw) =>
        DateTime.TryParse(raw, out _) ? null : $"{field.Label} must be a valid date and time.";
}
