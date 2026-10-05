using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// A <c>location-picker</c>: a plain text input holding <c>latitude,longitude</c>, complete and
/// accessible with no script. <c>wayfinder-location-picker.js</c> enhances it with a map and a
/// "use my current location" button, and stays in sync with the text input.
/// </summary>
internal static class GovUkLocationPickerField
{
    private const string DefaultHint = "Latitude and longitude in decimal degrees, for example 51.5074, -0.1278";

    internal static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, _, describedBy, required, error) = GovUkFields.Common(field, errors);
        var hintId = $"{id}-hint";
        var hintText = string.IsNullOrWhiteSpace(field.Hint) ? DefaultHint : field.Hint;
        var describedByIds = string.Join(' ', new[] { hintId, error is null ? null : $"{id}-error" }.Where(v => v is not null));
        var errorClass = error is null ? "" : " govuk-input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}" data-wayfinder-location-picker>
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              <div id="{hintId}" class="govuk-hint">{GovUk.Esc(hintText)}</div>
              {GovUkFields.ErrorMessage($"{id}-error", error)}
              <input class="govuk-input govuk-input--width-20{errorClass}" id="{id}" name="{name}" type="text"
                     inputmode="text" autocomplete="off" spellcheck="false" value="{GovUk.Esc(field.Value?.ToString() ?? "")}"
                     data-wayfinder-location-input aria-describedby="{describedByIds}" {required}>
            </div>
            """;
    }
}
