using System.Text;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// Renders an engine-produced <see cref="StepContent"/> as real <c>govuk-frontend</c> markup.
/// Simple by default — every <c>Component</c>/field type has a correct built-in renderer, so a
/// host needs zero registration to get a fully working form. Extendable by design — a host can
/// override any single type with a plain C# delegate via <see cref="RegisterComponent"/>/
/// <see cref="RegisterField"/>, no Razor/ViewEngine ceremony required, and still have nested
/// field rendering (inside an overridden fieldset, say) honour any other registered overrides.
/// </summary>
/// <remarks>
/// Stateless aside from its own override registrations — safe to register once as a singleton
/// and reuse across every request; nothing here holds per-request state (errors/problems are
/// threaded through as plain parameters, not instance fields).
/// </remarks>
public sealed class GovUkComponentRenderer
{
    private readonly Dictionary<string, Func<ComponentRenderPayload, Func<FieldRenderPayload, string>, string>> _componentOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Func<FieldRenderPayload, IReadOnlyDictionary<string, string>, string>> _fieldOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overrides rendering for every component of the given <c>Type</c> (e.g. <c>"fieldset"</c>).
    /// The supplied delegate receives a field-renderer callback so its own nested fields still
    /// honour any <see cref="RegisterField"/> overrides.</summary>
    public void RegisterComponent(string type, Func<ComponentRenderPayload, Func<FieldRenderPayload, string>, string> render) =>
        _componentOverrides[type] = render;

    /// <summary>Overrides rendering for every field of the given <c>FieldType</c> (e.g. <c>"text"</c>).</summary>
    public void RegisterField(string type, Func<FieldRenderPayload, IReadOnlyDictionary<string, string>, string> render) =>
        _fieldOverrides[type] = render;

    /// <summary>True for stages whose render already carries its own GOV.UK panel (which is
    /// itself an &lt;h1&gt;) — the caller should skip rendering a separate page heading.</summary>
    public static bool HasPanel(StepContent render) => render.Components.Any(c => c.Type == "panel");

    public string RenderForm(
        StepContent render,
        IReadOnlyList<ServiceRequestProblem> problems,
        string formAction,
        int stateVersion)
    {
        var errors = problems
            .Where(p => !string.IsNullOrWhiteSpace(p.FieldKey))
            .GroupBy(p => p.FieldKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal);

        string RenderFieldLocal(FieldRenderPayload field) =>
            _fieldOverrides.TryGetValue(field.FieldType, out var overridden)
                ? overridden(field, errors)
                : GovUkFields.Render(field, errors);

        var sb = new StringBuilder();

        if (problems.Count > 0)
        {
            sb.Append("""<div class="govuk-error-summary" data-module="govuk-error-summary"><div role="alert"><h2 class="govuk-error-summary__title">There is a problem</h2><div class="govuk-error-summary__body"><ul class="govuk-list govuk-error-summary__list">""");
            foreach (var problem in problems)
            {
                var href = string.IsNullOrWhiteSpace(problem.FieldKey) ? "" : $" href=\"#{GovUk.FieldName(problem.FieldKey)}\"";
                sb.Append($"<li><a{href}>{GovUk.Esc(problem.Message)}</a></li>");
            }
            sb.Append("</ul></div></div></div>");
        }

        sb.Append($"<form method=\"post\" action=\"{GovUk.Esc(formAction)}\">");
        sb.Append($"<input type=\"hidden\" name=\"stateVersion\" value=\"{stateVersion}\" />");

        foreach (var component in render.Components)
        {
            sb.Append(RenderComponent(component, RenderFieldLocal));
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
                    $"""<button class="govuk-button{styleClass}" data-module="govuk-button" type="submit" name="action" value="{GovUk.Esc(action.ActionKey)}">{GovUk.Esc(action.Label)}</button>""");
            }
            sb.Append("</div>");
        }

        sb.Append("</form>");
        return sb.ToString();
    }

    private string RenderComponent(ComponentRenderPayload component, Func<FieldRenderPayload, string> renderField)
    {
        var inner = _componentOverrides.TryGetValue(component.Type, out var overridden)
            ? overridden(component, renderField)
            : GovUkComponents.Render(component, renderField);

        // Live visibility: wrap whatever rendered (built-in or overridden) in the showWhen data
        // attribute a client-side runtime can re-evaluate, plus the server-evaluated hidden
        // state — the same trick regardless of which renderer produced the inner markup.
        if (string.IsNullOrEmpty(component.ShowWhen))
        {
            return inner;
        }

        var hiddenAttr = component.Hidden ? " hidden" : "";
        return $"""<div data-wayfinder-show-when="{GovUk.Esc(component.ShowWhen)}"{hiddenAttr}>{inner}</div>""";
    }
}
