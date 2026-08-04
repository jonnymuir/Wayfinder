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

    /// <summary>Builds a <c>FieldKey → message</c> lookup from a raw <see cref="ServiceRequestProblem"/>
    /// list — the shape every render method here takes, and what a host with its own error-summary
    /// rendering (e.g. Wayfinder.Umbraco's own tag helpers) needs to build once per request.</summary>
    public static IReadOnlyDictionary<string, string> BuildErrorLookup(IReadOnlyList<ServiceRequestProblem> problems) =>
        problems
            .Where(p => !string.IsNullOrWhiteSpace(p.FieldKey))
            .GroupBy(p => p.FieldKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal);

    /// <summary>Renders a single field — an editable input, or (for content-only types like
    /// <c>body</c>/<c>warning-text</c> nested inside a fieldset) inline content. The primitive a
    /// host with its own surrounding form chrome (its own stage-form/error-summary rendering)
    /// calls directly per field, without needing <see cref="RenderForm"/>'s whole-page shape.</summary>
    public string RenderField(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors) =>
        _fieldOverrides.TryGetValue(field.FieldType, out var overridden)
            ? overridden(field, errors)
            : GovUkFields.Render(field, errors);

    /// <summary>Renders a single top-level component, including its own nested fields (via
    /// <see cref="RenderField"/>, so overrides apply consistently) and the showWhen/Hidden
    /// wrapping. The primitive a host with its own surrounding form chrome calls directly per
    /// component, without needing <see cref="RenderForm"/>'s whole-page shape.</summary>
    public string RenderComponent(ComponentRenderPayload component, IReadOnlyDictionary<string, string> errors)
    {
        // This host renders server-side only, with no client-side runtime to flip `required`
        // back on if showWhen later evaluates true — so a hidden component's fields must never
        // carry `required` in the markup at all, or the browser's own HTML5 constraint
        // validation silently blocks the whole form's submission (invisible, unreachable
        // required fields, but the browser still counts them). Server-side validation already
        // treats a hidden field's Required as inapplicable (see FieldValueValidator's
        // hiddenFieldKeys) — this keeps the client-side guard consistent with that, rather than
        // stricter than the server it's meant to merely mirror. A host with its own live-form
        // runtime that re-enables `required` client-side once showWhen flips true (Wayfinder.Umbraco
        // has one) isn't undermined by this — it can only ever make an already-permissive
        // attribute stricter at runtime, never bypass a constraint that was already there.
        string RenderFieldEffective(FieldRenderPayload field) =>
            RenderField(component.Hidden ? field with { Required = false } : field, errors);

        var inner = _componentOverrides.TryGetValue(component.Type, out var overridden)
            ? overridden(component, RenderFieldEffective)
            : GovUkComponents.Render(component, RenderFieldEffective);

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

    /// <summary>Renders a complete stage as a standalone GOV.UK page body — error summary, a
    /// <c>&lt;form&gt;</c> wrapping every component, and the action buttons. For a host with no
    /// surrounding form chrome of its own (a minimal-API host, say); a host that already has its
    /// own (Wayfinder.Umbraco's <c>StageFormTagHelper</c>/<c>ErrorSummaryTagHelper</c>, for
    /// example) calls <see cref="RenderComponent"/>/<see cref="RenderField"/> directly instead.</summary>
    public string RenderForm(
        StepContent render,
        IReadOnlyList<ServiceRequestProblem> problems,
        string formAction,
        int stateVersion)
    {
        var errors = BuildErrorLookup(problems);
        var sb = new StringBuilder();

        if (problems.Count > 0)
        {
            sb.Append("""<div class="govuk-error-summary" data-module="govuk-error-summary"><div role="alert"><h2 class="govuk-error-summary__title">There is a problem</h2><div class="govuk-error-summary__body"><ul class="govuk-list govuk-error-summary__list">""");
            foreach (var problem in problems)
            {
                // Targets the input's own id, which is the bare field key — GovUk.FieldName's
                // "field:{fieldKey}" is the name= attribute's own convention (posted-form
                // routing), never what's actually in a rendered id="" to jump focus to.
                var href = string.IsNullOrWhiteSpace(problem.FieldKey) ? "" : $" href=\"#{GovUk.Esc(problem.FieldKey)}\"";
                sb.Append($"<li><a{href}>{GovUk.Esc(problem.Message)}</a></li>");
            }
            sb.Append("</ul></div></div></div>");
        }

        // multipart/form-data unconditionally, not only when a file-upload field is present —
        // simpler than detecting it, and file-free submissions work identically either way.
        sb.Append($"<form method=\"post\" action=\"{GovUk.Esc(formAction)}\" enctype=\"multipart/form-data\">");
        sb.Append($"<input type=\"hidden\" name=\"stateVersion\" value=\"{stateVersion}\" />");

        foreach (var component in render.Components)
        {
            sb.Append(RenderComponent(component, errors));
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
}
