using System.Net;
using System.Text;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// Renders an engine-produced <see cref="StepContent"/> (already-resolved
/// <see cref="ComponentRenderPayload"/>/<see cref="FieldRenderPayload"/> values, not raw
/// <c>Component</c> definitions) as real GOV.UK Design System markup — the actual
/// <c>govuk-frontend</c> component classes/structure copied from that package's own compiled
/// examples (<c>node_modules/govuk-frontend/dist/govuk/components/*&#47;template-*.html</c>),
/// not a lookalike. <see cref="Wayfinder.ReferenceApp.PageShell"/> links the real compiled
/// <c>govuk-frontend.min.css</c>/<c>.min.js</c> this markup depends on.
/// </summary>
public static class ComponentHtmlRenderer
{
    public static string RenderForm(
        StepContent render,
        IReadOnlyList<ServiceRequestProblem> problems,
        string formAction,
        int stateVersion)
    {
        var sb = new StringBuilder();

        if (problems.Count > 0)
        {
            sb.Append("""<div class="govuk-error-summary" data-module="govuk-error-summary"><div role="alert"><h2 class="govuk-error-summary__title">There is a problem</h2><div class="govuk-error-summary__body"><ul class="govuk-list govuk-error-summary__list">""");
            foreach (var problem in problems)
            {
                var href = string.IsNullOrWhiteSpace(problem.FieldKey) ? "" : $" href=\"#field:{Esc(problem.FieldKey)}\"";
                sb.Append($"<li><a{href}>{Esc(problem.Message)}</a></li>");
            }
            sb.Append("</ul></div></div></div>");
        }

        sb.Append($"<form method=\"post\" action=\"{Esc(formAction)}\">");
        sb.Append($"<input type=\"hidden\" name=\"stateVersion\" value=\"{stateVersion}\" />");

        foreach (var component in render.Components)
        {
            sb.Append(RenderComponent(component));
        }

        if (render.AvailableActions.Count > 0)
        {
            sb.Append("""<div class="govuk-button-group">""");
            foreach (var action in render.AvailableActions)
            {
                var styleClass = action.Style switch
                {
                    "secondary" => " govuk-button--secondary",
                    "destructive" => " govuk-button--warning",
                    _ => ""
                };
                sb.Append(
                    $"""<button class="govuk-button{styleClass}" data-module="govuk-button" type="submit" name="action" value="{Esc(action.ActionKey)}">{Esc(action.Label)}</button>""");
            }
            sb.Append("</div>");
        }

        sb.Append("</form>");
        return sb.ToString();
    }

    private static string RenderComponent(ComponentRenderPayload component) => component.Type switch
    {
        "fieldset" => $"""
            <fieldset class="govuk-fieldset">
              {(string.IsNullOrWhiteSpace(component.Legend) ? "" : $"""<legend class="govuk-fieldset__legend govuk-fieldset__legend--m">{Esc(component.Legend)}</legend>""")}
              {string.Join("\n", component.Fields.Select(f => RenderField(f, readOnly: false)))}
            </fieldset>
            """,
        "summary-list" => $"""
            {(string.IsNullOrWhiteSpace(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{Esc(component.Title)}</h2>""")}
            <dl class="govuk-summary-list">
              {string.Join("\n", component.Fields.Select(f => RenderField(f, readOnly: true)))}
            </dl>
            """,
        "panel" => $"""
            <div class="govuk-panel govuk-panel--confirmation">
              <h1 class="govuk-panel__title">{Esc(component.Heading)}</h1>
            </div>
            """,
        "body" => $"""<p class="govuk-body">{component.Content}</p>""", // Content is already sanitized by the engine.
        "heading" => component.Level switch
        {
            1 => $"""<h1 class="govuk-heading-xl">{Esc(component.Content)}</h1>""",
            2 => $"""<h2 class="govuk-heading-l">{Esc(component.Content)}</h2>""",
            3 => $"""<h3 class="govuk-heading-m">{Esc(component.Content)}</h3>""",
            _ => $"""<h4 class="govuk-heading-s">{Esc(component.Content)}</h4>"""
        },
        _ => ""
    };

    /// <summary>True for stages whose render already carries its own GOV.UK panel (which is
    /// itself an &lt;h1&gt;) — the caller should skip rendering a separate page heading.</summary>
    public static bool HasPanel(StepContent render) => render.Components.Any(c => c.Type == "panel");

    private static string RenderField(FieldRenderPayload field, bool readOnly)
    {
        var value = field.Value?.ToString() ?? "";

        if (readOnly)
        {
            return $"""
                <div class="govuk-summary-list__row">
                  <dt class="govuk-summary-list__key">{Esc(field.Label)}</dt>
                  <dd class="govuk-summary-list__value">{Esc(FormatSummaryValue(field, value))}</dd>
                </div>
                """;
        }

        var name = $"field:{field.FieldKey}";
        var hint = string.IsNullOrWhiteSpace(field.Hint)
            ? ""
            : $"""<div id="{Esc(name)}-hint" class="govuk-hint">{Esc(field.Hint)}</div>""";
        var describedBy = string.IsNullOrWhiteSpace(field.Hint) ? "" : $" aria-describedby=\"{Esc(name)}-hint\"";
        // GOV.UK Design System guidance prefers server-side validation (a real
        // govuk-error-summary) over relying on the browser's own `required` tooltips — but
        // Wayfinder.Engine.Services.ProcessManagerEngine.Advance doesn't enforce Required
        // server-side at all (it only forwards the flag into the render payload), so dropping
        // `required` here would leave nothing stopping an empty submit. Keep it as the
        // pragmatic client-side guard this app actually relies on.
        var required = field.Required ? "required" : "";

        return field.FieldType switch
        {
            "boolean" => $"""
                <div class="govuk-form-group">
                  <div class="govuk-checkboxes" data-module="govuk-checkboxes">
                    <div class="govuk-checkboxes__item">
                      <input class="govuk-checkboxes__input" id="{Esc(name)}" name="{Esc(name)}" type="checkbox" value="true" {(value == "true" ? "checked" : "")} {required}>
                      <label class="govuk-label govuk-checkboxes__label" for="{Esc(name)}">{Esc(field.Label)}</label>
                    </div>
                  </div>
                </div>
                """,
            "date" => RenderDateField(field, name, value, hint, required),
            "number" or "decimal" => $"""
                <div class="govuk-form-group">
                  <label class="govuk-label" for="{Esc(name)}">{Esc(field.Label)}</label>
                  {hint}
                  <input class="govuk-input govuk-input--width-5" id="{Esc(name)}" name="{Esc(name)}" type="text" inputmode="{(field.FieldType == "decimal" ? "decimal" : "numeric")}" value="{Esc(value)}"{describedBy} {required}>
                </div>
                """,
            "email" => $"""
                <div class="govuk-form-group">
                  <label class="govuk-label" for="{Esc(name)}">{Esc(field.Label)}</label>
                  {hint}
                  <input class="govuk-input" id="{Esc(name)}" name="{Esc(name)}" type="email" autocomplete="email" spellcheck="false" value="{Esc(value)}"{describedBy} {required}>
                </div>
                """,
            "textarea" => $"""
                <div class="govuk-form-group">
                  <label class="govuk-label" for="{Esc(name)}">{Esc(field.Label)}</label>
                  {hint}
                  <textarea class="govuk-textarea" id="{Esc(name)}" name="{Esc(name)}" rows="5"{describedBy} {required}>{Esc(value)}</textarea>
                </div>
                """,
            _ => $"""
                <div class="govuk-form-group">
                  <label class="govuk-label" for="{Esc(name)}">{Esc(field.Label)}</label>
                  {hint}
                  <input class="govuk-input" id="{Esc(name)}" name="{Esc(name)}" type="text" value="{Esc(value)}"{describedBy} {required}>
                </div>
                """
        };
    }

    /// <summary>
    /// The real GOV.UK date-input component: three separate day/month/year text inputs, not a
    /// native <c>&lt;input type="date"&gt;</c> (GOV.UK Design System deliberately avoids native
    /// date pickers). The engine's <see cref="FieldRenderPayload"/> only carries one string
    /// value per field, so the three parts round-trip through a single ISO ("yyyy-MM-dd")
    /// field value — see <c>Program.cs</c>'s <c>CoerceFieldValues</c> for the reverse direction.
    /// </summary>
    private static string RenderDateField(FieldRenderPayload field, string name, string isoValue, string hint, string required)
    {
        var (day, month, year) = SplitIsoDate(isoValue);
        return $"""
            <div class="govuk-form-group">
              <fieldset class="govuk-fieldset" role="group"{(string.IsNullOrWhiteSpace(field.Hint) ? "" : $" aria-describedby=\"{Esc(name)}-hint\"")}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--s">{Esc(field.Label)}</legend>
                {hint}
                <div class="govuk-date-input" id="{Esc(name)}">
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{Esc(name)}-day">Day</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-2" id="{Esc(name)}-day" name="{Esc(name)}-day" type="text" inputmode="numeric" value="{Esc(day)}" {required}>
                    </div>
                  </div>
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{Esc(name)}-month">Month</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-2" id="{Esc(name)}-month" name="{Esc(name)}-month" type="text" inputmode="numeric" value="{Esc(month)}" {required}>
                    </div>
                  </div>
                  <div class="govuk-date-input__item">
                    <div class="govuk-form-group">
                      <label class="govuk-label govuk-date-input__label" for="{Esc(name)}-year">Year</label>
                      <input class="govuk-input govuk-date-input__input govuk-input--width-4" id="{Esc(name)}-year" name="{Esc(name)}-year" type="text" inputmode="numeric" value="{Esc(year)}" {required}>
                    </div>
                  </div>
                </div>
              </fieldset>
            </div>
            """;
    }

    private static (string Day, string Month, string Year) SplitIsoDate(string isoValue)
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

    private static string FormatSummaryValue(FieldRenderPayload field, string value) => field.FieldType switch
    {
        "boolean" => value == "true" ? "Yes" : "No",
        "date" => DateOnly.TryParse(value, out var date) ? date.ToString("d MMMM yyyy") : value,
        _ => value
    };

    public static string Esc(string? value) => WebUtility.HtmlEncode(value ?? "");
}
