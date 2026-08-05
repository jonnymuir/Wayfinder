using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// Built-in real <c>govuk-frontend</c> markup for every <see cref="FieldRenderPayload.FieldType"/>
/// Wayfinder's <c>Component</c> catalog defines. Called by <see cref="GovUkComponentRenderer"/>'s
/// dispatch for any type without a host override.
/// </summary>
public static class GovUkFields
{
    /// <summary>
    /// Renders one editable field, including its <c>govuk-error-message</c> when
    /// <paramref name="errors"/> carries an entry for its <see cref="FieldRenderPayload.FieldKey"/>.
    /// Always an editable control — a summary-list row's own read-only display is
    /// <see cref="RenderSummaryRow"/>, a separate, non-overridable rendering context (matching
    /// how "fieldset" vs "summary-list" is what decides editable-vs-display, not any property
    /// on the field itself).
    /// </summary>
    public static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var inline = RenderInlineContentType(field);
        if (inline is not null)
        {
            return inline;
        }

        return field.FieldType switch
        {
            "boolean" => RenderBoolean(field, errors),
            "date" => RenderDate(field, errors),
            "number" or "decimal" => RenderNumber(field, errors),
            "email" => RenderEmail(field, errors),
            "textarea" => RenderTextarea(field, errors),
            "select" => RenderSelect(field, errors),
            "radio" => RenderRadio(field, errors),
            "checkboxlist" => RenderCheckboxList(field, errors),
            "slider" => RenderSlider(field, errors),
            "file-upload" => RenderFileUpload(field, errors),
            "guidance-checklist" => RenderGuidanceChecklist(field, errors),
            _ => RenderText(field, errors),
        };
    }

    /// <summary>
    /// <paramref name="sourceStateKey"/> is the summary-list's own default change target
    /// (<c>ComponentRenderPayload.SourceStateKey</c>) — used when this field doesn't declare
    /// its own <see cref="FieldRenderPayload.ChangeStateKey"/>, for a summary spanning rows
    /// captured on more than one earlier stage.
    /// </summary>
    public static string RenderSummaryRow(FieldRenderPayload field, string? sourceStateKey = null)
    {
        var value = field.Value?.ToString() ?? "";
        var changeTarget = field.ChangeStateKey ?? sourceStateKey;
        // A real <a href> can't safely re-trigger this — "change:" advances StateVersion
        // through the exact same POST/Advance path as every other action on this stage, so it
        // needs the form's current hidden stateVersion field, not a bare GET link. A button
        // reset to look like the real govuk-frontend "Change" link (see summary-list's own
        // template-with-actions.html) gets the same visual result via that same mechanism.
        // formnovalidate is essential, not cosmetic: without it, a plain type="submit" button
        // is blocked by the browser's own HTML5 constraint validation against *every* required
        // field still on this stage (e.g. declaration's own confirmation checkbox) before the
        // click even reaches the server — confirmed live, the click fires but no request goes
        // out at all. The entire point of "Change" is to let the author go back and fix an
        // earlier answer without first satisfying this stage's own requirements.
        var actionsCell = string.IsNullOrWhiteSpace(changeTarget)
            ? ""
            : $"""
                <dd class="govuk-summary-list__actions">
                  <button type="submit" formnovalidate name="action" value="{GovUk.Esc($"change:{changeTarget}")}" class="govuk-link" style="background:none;border:0;padding:0;font:inherit;cursor:pointer;">Change<span class="govuk-visually-hidden"> {GovUk.Esc(field.Label.ToLowerInvariant())}</span></button>
                </dd>
                """;
        return $"""
            <div class="govuk-summary-list__row">
              <dt class="govuk-summary-list__key">{GovUk.Esc(field.Label)}</dt>
              <dd class="govuk-summary-list__value">{GovUk.Esc(FormatSummaryValue(field, value))}</dd>
              {actionsCell}
            </div>
            """;
    }

    private static string FormatSummaryValue(FieldRenderPayload field, string value) => field.FieldType switch
    {
        "boolean" => value == "true" ? "Yes" : "No",
        "date" => DateOnly.TryParse(value, out var date) ? date.ToString("d MMMM yyyy") : value,
        _ => value
    };

    /// <summary>
    /// Content-only field types (<c>inset-text</c>/<c>warning-text</c>/<c>details</c>/
    /// <c>notification-banner</c>/<c>body</c>/<c>heading</c> nested inside a fieldset) — not real
    /// form controls, no <c>govuk-form-group</c> wrapper, no error state. <c>null</c> for any
    /// other type, so the caller falls through to the normal input dispatch.
    /// </summary>
    private static string? RenderInlineContentType(FieldRenderPayload field)
    {
        var content = field.Content;
        var label = field.Label;

        return field.FieldType switch
        {
            "inset-text" when !string.IsNullOrEmpty(content) =>
                $"""<div class="govuk-inset-text">{content}</div>""",
            "warning-text" when !string.IsNullOrEmpty(content) =>
                $"""
                <div class="govuk-warning-text">
                  <span class="govuk-warning-text__icon" aria-hidden="true">!</span>
                  <strong class="govuk-warning-text__text">
                    <span class="govuk-visually-hidden">Warning</span>
                    {content}
                  </strong>
                </div>
                """,
            "details" when !string.IsNullOrEmpty(content) =>
                $"""
                <details class="govuk-details">
                  <summary class="govuk-details__summary">
                    <span class="govuk-details__summary-text">{GovUk.Esc(string.IsNullOrEmpty(label) ? "More information" : label)}</span>
                  </summary>
                  <div class="govuk-details__text">{content}</div>
                </details>
                """,
            "notification-banner" when !string.IsNullOrEmpty(content) =>
                $"""
                <div class="govuk-notification-banner" role="region" aria-labelledby="wayfinder-banner-title-{GovUk.Esc(field.FieldKey)}">
                  <div class="govuk-notification-banner__header">
                    <h2 class="govuk-notification-banner__title" id="wayfinder-banner-title-{GovUk.Esc(field.FieldKey)}">{GovUk.Esc(string.IsNullOrEmpty(label) ? "Information" : label)}</h2>
                  </div>
                  <div class="govuk-notification-banner__content"><p class="govuk-body">{content}</p></div>
                </div>
                """,
            "body" when !string.IsNullOrEmpty(content) => $"""<p class="govuk-body">{content}</p>""",
            "heading" when !string.IsNullOrEmpty(content) => $"""<h2 class="govuk-heading-m">{content}</h2>""",
            "inset-text" or "warning-text" or "details" or "notification-banner" or "body" or "heading" => "",
            _ => null,
        };
    }

    /// <summary>
    /// <paramref name="id"/> is the bare field key — what every <c>id</c>/<c>for</c>/
    /// <c>aria-describedby</c> reference uses, so a rendered field stays addressable by plain
    /// CSS ID selectors and doesn't need colon-escaping. <paramref name="name"/> is
    /// <see cref="GovUk.FieldName"/>'s <c>field:{fieldKey}</c> form, used only for the
    /// <c>name</c> attribute a host's own form-submission parsing keys off — the two used to be
    /// conflated into one string here, which broke id-based selectors even though name-based
    /// posting was already correct.
    /// </summary>
    private static (string Id, string Name, string Hint, string DescribedBy, string Required, string? Error) Common(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var id = field.FieldKey;
        var name = GovUk.FieldName(field.FieldKey);
        var hintId = $"{id}-hint";
        var errorId = $"{id}-error";
        var hasHint = !string.IsNullOrWhiteSpace(field.Hint);
        var hasError = errors.TryGetValue(field.FieldKey, out var error);

        var describedByIds = string.Join(' ', new[] { hasHint ? hintId : null, hasError ? errorId : null }.Where(v => v is not null));
        var describedBy = describedByIds.Length == 0 ? "" : $" aria-describedby=\"{describedByIds}\"";

        var hint = hasHint ? $"""<div id="{hintId}" class="govuk-hint">{GovUk.Esc(field.Hint)}</div>""" : "";
        var required = field.Required ? "required" : "";

        return (id, name, hint, describedBy, required, hasError ? error : null);
    }

    private static string ErrorMessage(string errorId, string? error) =>
        error is null ? "" : $"""<p class="govuk-error-message" id="{errorId}"><span class="govuk-visually-hidden">Error:</span> {GovUk.Esc(error)}</p>""";

    private static string RenderText(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var errorClass = error is null ? "" : " govuk-input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              <input class="govuk-input{errorClass}" id="{id}" name="{name}" type="text" value="{GovUk.Esc(value)}"{describedBy} {required}>
            </div>
            """;
    }

    private static string RenderNumber(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var errorClass = error is null ? "" : " govuk-input--error";
        var inputMode = field.FieldType == "decimal" ? "decimal" : "numeric";
        var prefix = string.IsNullOrEmpty(field.Prefix) ? "" : $"""
            <div class="govuk-input__prefix" aria-hidden="true">{GovUk.Esc(field.Prefix)}</div>
            """;
        var suffix = string.IsNullOrEmpty(field.Suffix) ? "" : $"""
            <div class="govuk-input__suffix" aria-hidden="true">{GovUk.Esc(field.Suffix)}</div>
            """;
        var wrapped = string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix)
            ? $"""<input class="govuk-input govuk-input--width-5{errorClass}" id="{id}" name="{name}" type="text" inputmode="{inputMode}" value="{GovUk.Esc(value)}"{describedBy} {required}>"""
            : $"""
                <div class="govuk-input__wrapper">
                  {prefix}<input class="govuk-input govuk-input--width-5{errorClass}" id="{id}" name="{name}" type="text" inputmode="{inputMode}" value="{GovUk.Esc(value)}"{describedBy} {required}>{suffix}
                </div>
                """;
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              {wrapped}
            </div>
            """;
    }

    private static string RenderEmail(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var errorClass = error is null ? "" : " govuk-input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              <input class="govuk-input{errorClass}" id="{id}" name="{name}" type="email" autocomplete="email" spellcheck="false" value="{GovUk.Esc(value)}"{describedBy} {required}>
            </div>
            """;
    }

    private static string RenderTextarea(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var errorClass = error is null ? "" : " govuk-textarea--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              <textarea class="govuk-textarea{errorClass}" id="{id}" name="{name}" rows="5"{describedBy} {required}>{GovUk.Esc(value)}</textarea>
            </div>
            """;
    }

    private static string RenderBoolean(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              {hint}
              {ErrorMessage($"{id}-error", error)}
              <div class="govuk-checkboxes" data-module="govuk-checkboxes">
                <div class="govuk-checkboxes__item">
                  <input class="govuk-checkboxes__input" id="{id}" name="{name}" type="checkbox" value="true" {(value == "true" ? "checked" : "")}{describedBy} {required}>
                  <label class="govuk-label govuk-checkboxes__label" for="{id}">{GovUk.Esc(field.Label)}</label>
                </div>
              </div>
            </div>
            """;
    }

    private static string RenderDate(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, _, required, error) = Common(field, errors);
        var (day, month, year) = GovUk.SplitIsoDate(field.Value?.ToString());
        var errorClass = error is null ? "" : " govuk-input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <fieldset class="govuk-fieldset" role="group"{(string.IsNullOrWhiteSpace(field.Hint) ? "" : $" aria-describedby=\"{id}-hint\"")}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--s">{GovUk.Esc(field.Label)}</legend>
                {hint}
                {ErrorMessage($"{id}-error", error)}
                <div class="govuk-date-input" id="{id}">
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{id}-day">Day</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-2{errorClass}" id="{id}-day" name="{name}-day" type="text" inputmode="numeric" value="{GovUk.Esc(day)}" {required}>
                    </div>
                  </div>
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{id}-month">Month</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-2{errorClass}" id="{id}-month" name="{name}-month" type="text" inputmode="numeric" value="{GovUk.Esc(month)}" {required}>
                    </div>
                  </div>
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{id}-year">Year</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-4{errorClass}" id="{id}-year" name="{name}-year" type="text" inputmode="numeric" value="{GovUk.Esc(year)}" {required}>
                    </div>
                  </div>
                </div>
              </fieldset>
            </div>
            """;
    }

    private static string RenderSelect(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var errorClass = error is null ? "" : " govuk-select--error";
        var options = (field.Options ?? Array.Empty<string>())
            .Select(o => $"""<option value="{GovUk.Esc(o)}"{(string.Equals(o, value, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{GovUk.Esc(o)}</option>""");
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              <select class="govuk-select{errorClass}" id="{id}" name="{name}"{describedBy} {required}>
                <option value="">-- Select --</option>
                {string.Join("\n", options)}
              </select>
            </div>
            """;
    }

    private static string RenderRadio(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var value = field.Value?.ToString() ?? "";
        var items = (field.Options ?? Array.Empty<string>()).Select(option =>
        {
            var optionId = $"{id}-{OptionIdFragment(option)}";
            var isChecked = string.Equals(option, value, StringComparison.OrdinalIgnoreCase);
            return $"""
                <div class="govuk-radios__item">
                  <input class="govuk-radios__input" type="radio" id="{optionId}" name="{name}" value="{GovUk.Esc(option)}" {(isChecked ? "checked" : "")} {required}>
                  <label class="govuk-label govuk-radios__label" for="{optionId}">{GovUk.Esc(option)}</label>
                </div>
                """;
        });
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <fieldset class="govuk-fieldset"{describedBy}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--s">{GovUk.Esc(field.Label)}</legend>
                {hint}
                {ErrorMessage($"{id}-error", error)}
                <div class="govuk-radios" data-module="govuk-radios">
                  {string.Join("\n", items)}
                </div>
              </fieldset>
            </div>
            """;
    }

    private static string RenderCheckboxList(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, _, error) = Common(field, errors);
        var checkedValues = (field.Value?.ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = (field.Options ?? Array.Empty<string>()).Select(option =>
        {
            var optionId = $"{id}-{OptionIdFragment(option)}";
            return $"""
                <div class="govuk-checkboxes__item">
                  <input class="govuk-checkboxes__input" type="checkbox" id="{optionId}" name="{name}[]" value="{GovUk.Esc(option)}" {(checkedValues.Contains(option) ? "checked" : "")}>
                  <label class="govuk-label govuk-checkboxes__label" for="{optionId}">{GovUk.Esc(option)}</label>
                </div>
                """;
        });
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <fieldset class="govuk-fieldset"{describedBy}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--s">{GovUk.Esc(field.Label)}</legend>
                {hint}
                {ErrorMessage($"{id}-error", error)}
                <div class="govuk-checkboxes" data-module="govuk-checkboxes">
                  {string.Join("\n", items)}
                </div>
              </fieldset>
            </div>
            """;
    }

    /// <summary>
    /// Real GOV.UK Design System has no official "slider" component, so this is Wayfinder's own —
    /// a live-updating <c>wayfinder-slider__*</c>-classed range input with a progressive-enhancement
    /// hook (<c>data-wayfinder-slider-input</c>/<c>data-wayfinder-slider-value</c>) a host wires its
    /// own JS to, same as govuk-frontend's own components need a host to load govuk-frontend's JS.
    /// This is the gold-standard rendering — hosts don't need their own override for this type.
    /// </summary>
    private static string RenderSlider(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var min = field.Min ?? 0;
        var max = field.Max ?? 100;
        var value = string.IsNullOrEmpty(field.Value?.ToString()) ? min.ToString() : field.Value!.ToString()!;
        var prefix = field.Prefix ?? "";
        var suffix = field.Suffix ?? "";
        var errorClass = error is null ? "" : " wayfinder-slider__input--error";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}" data-wayfinder-slider>
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
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

    /// <summary>
    /// A plain, synchronous <c>govuk-file-upload</c> — posted as part of the normal form submit,
    /// with the host saving it and swapping the value for a reference before it reaches the
    /// engine (the engine itself never sees raw bytes). Deliberately not Wayfinder.Umbraco's
    /// async progressive-upload-with-token pattern — that needs its own JS runtime this package
    /// doesn't ship.
    /// </summary>
    private static string RenderFileUpload(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = Common(field, errors);
        var alreadyUploaded = !string.IsNullOrEmpty(field.Value?.ToString());
        var accept = field.AcceptedFileTypes is { Count: > 0 }
            ? $" accept=\"{GovUk.Esc(string.Join(",", field.AcceptedFileTypes))}\""
            : "";
        var errorClass = error is null ? "" : " govuk-file-upload--error";
        var uploadedNotice = alreadyUploaded
            ? $"""<p class="govuk-body">Currently uploaded: {GovUk.Esc(field.Value?.ToString())}</p>"""
            : "";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {ErrorMessage($"{id}-error", error)}
              {uploadedNotice}
              <input class="govuk-file-upload{errorClass}" id="{id}" name="{name}" type="file"{accept}{describedBy} {(alreadyUploaded ? "" : required)}>
            </div>
            """;
    }

    private static string RenderGuidanceChecklist(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, _, error) = Common(field, errors);
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
                {ErrorMessage($"{id}-error", error)}
                <p class="govuk-body">{completed} of {items.Count} guidance articles completed</p>
                <div class="govuk-checkboxes" data-module="govuk-checkboxes">
                  {string.Join("\n", rows)}
                </div>
              </fieldset>
            </div>
            """;
    }

    private static string OptionIdFragment(string option) =>
        string.Concat(option.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
