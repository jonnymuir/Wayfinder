using System.Globalization;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

internal static class GovUkSliderField
{
    /// <summary>
    /// Real GOV.UK Design System has no official "slider" component, so this is Wayfinder's own —
    /// a live-updating <c>wayfinder-slider__*</c>-classed range input with a progressive-enhancement
    /// hook (<c>data-wayfinder-slider-input</c>/<c>data-wayfinder-slider-value</c>) a host wires its
    /// own JS to, same as govuk-frontend's own components need a host to load govuk-frontend's JS.
    /// This is the gold-standard rendering — hosts don't need their own override for this type.
    /// </summary>
    internal static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = GovUkFields.Common(field, errors);
        var min = field.Min ?? 0;
        var max = field.Max ?? 100;
        var value = string.IsNullOrEmpty(field.Value?.ToString()) ? min.ToString(CultureInfo.InvariantCulture) : field.Value!.ToString()!;
        var prefix = field.Prefix ?? "";
        var suffix = field.Suffix ?? "";
        var errorClass = error is null ? "" : " wayfinder-slider__input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}" data-wayfinder-slider>
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {GovUkFields.ErrorMessage($"{id}-error", error)}
              <div class="wayfinder-slider__row">
                <input class="wayfinder-slider__input{errorClass}"
                       type="range" id="{id}" name="{name}" value="{GovUk.Esc(value)}"
                       data-label="{GovUk.Esc(field.Label)}" data-wayfinder-slider-input{describedBy} {required}
                       min="{min}" max="{max}" step="{field.Step ?? 1}" />
                <span class="wayfinder-slider__value" data-wayfinder-slider-value
                      data-prefix="{GovUk.Esc(prefix)}" data-suffix="{GovUk.Esc(suffix)}" aria-hidden="true">{GovUk.Esc(prefix)}{GovUk.Esc(value)}{GovUk.Esc(suffix)}</span>
              </div>
              <div class="wayfinder-slider__bounds" aria-hidden="true">
                <span>{GovUk.Esc(prefix)}{min}{GovUk.Esc(suffix)}</span>
                <span>{GovUk.Esc(prefix)}{max}{GovUk.Esc(suffix)}</span>
              </div>
            </div>
            """;
    }
}
