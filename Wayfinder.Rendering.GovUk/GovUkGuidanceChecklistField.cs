using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Rendering.GovUk;

internal static class GovUkGuidanceChecklistField
{
    internal static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, _, error) = GovUkFields.Common(field, errors);
        var checkedValues = (field.Value?.ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = field.GuidanceItems ?? Array.Empty<GuidanceChecklistItem>();
        var completed = items.Count(i => checkedValues.Contains(i.Key));
        var rows = items.Select(item =>
        {
            var itemId = $"{id}-{item.Key}";
            return $"""
                <div class="govuk-checkboxes__item">
                  <input class="govuk-checkboxes__input" type="checkbox" id="{itemId}" name="{name}[]" value="{GovUk.Esc(item.Key)}" {(checkedValues.Contains(item.Key) ? "checked" : "")}>
                  <label class="govuk-label govuk-checkboxes__label" for="{itemId}">
                    <a class="govuk-link" href="{GovUk.Esc(item.Href)}" target="_blank" rel="noopener">{GovUk.Esc(item.Label)}</a>
                  </label>
                </div>
                """;
        });
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <fieldset class="govuk-fieldset"{describedBy}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--m">{GovUk.Esc(field.Label)}</legend>
                {hint}
                {GovUkFields.ErrorMessage($"{id}-error", error)}
                <p class="govuk-body">{completed} of {items.Count} guidance articles completed</p>
                <div class="govuk-checkboxes" data-module="govuk-checkboxes">
                  {string.Join("\n", rows)}
                </div>
              </fieldset>
            </div>
            """;
    }
}
