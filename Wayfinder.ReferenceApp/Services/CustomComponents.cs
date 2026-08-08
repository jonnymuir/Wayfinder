using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// A genuinely new component type, defined and rendered entirely outside Wayfinder's own
/// assembly — proof that a toolkit user really can extend the catalog, not just override
/// rendering of a built-in type. See docs/guides/extending-the-component-catalog.md.
/// A five-point confidence rating; deliberately declares no properties beyond
/// <see cref="InputComponent"/>'s own (FieldKey/Label/Hint/Required/...) — every one of those
/// already threads generically through <c>ProcessManagerEngine</c>'s rendering pipeline for
/// any <see cref="InputComponent"/> subtype, so this needs no engine changes at all to work.
/// </summary>
public sealed record RatingComponent : InputComponent;

/// <summary>
/// Registers <see cref="RatingComponent"/> — "what it is" (<see cref="Register"/>, called at
/// host startup before any blueprint is loaded) and "how it renders" (<see cref="RegisterRendering"/>,
/// a plain <c>GovUkComponentRenderer.RegisterField</c> override) are deliberately separate calls,
/// matching <c>ComponentTypeRegistry.Register</c>'s own doc comment: a third party can supply
/// either independently, or both.
/// </summary>
public static class CustomComponents
{
    public const string RatingDiscriminator = "rating";

    private static readonly IReadOnlyList<string> RatingScale =
        ["Very unconfident", "Unconfident", "Neutral", "Confident", "Very confident"];

    public static void Register() =>
        ComponentTypeRegistry.Register<RatingComponent>(new ComponentDescriptor
        {
            Discriminator = RatingDiscriminator,
            DisplayName = "Confidence rating",
            Category = ComponentCategory.Input,
            Description = "A five-point confidence rating, from \"Very unconfident\" to \"Very confident\".",
            ClrType = typeof(RatingComponent),
            IsInput = true,
            Properties =
            [
                new()
                {
                    Key = nameof(InputComponent.FieldKey), Title = "Field key",
                    Description = "Unique identifier for this field's captured value.",
                    ValueKind = ComponentPropertyValueKind.String, Required = true,
                },
                new()
                {
                    Key = nameof(InputComponent.Label), Title = "Label",
                    Description = "User-facing question displayed above the rating scale.",
                    ValueKind = ComponentPropertyValueKind.String, Required = true,
                },
                new()
                {
                    Key = nameof(InputComponent.Hint), Title = "Hint",
                    Description = "Optional helper text displayed below the label.",
                    ValueKind = ComponentPropertyValueKind.String,
                },
                new()
                {
                    Key = nameof(InputComponent.Required), Title = "Required",
                    Description = "Whether a rating must be given before advancing.",
                    ValueKind = ComponentPropertyValueKind.Boolean, Editor = "toggle",
                },
            ],
        });

    /// <summary>
    /// Real, accessible <c>govuk-frontend</c>-styled radios — hand-written here, in the host's
    /// own assembly, using nothing but the public <see cref="GovUk"/> helpers Wayfinder itself
    /// ships. <see cref="FieldRenderPayload"/>'s FieldKey/Label/Hint/Required/Value already carry
    /// everything this needs; a custom component whose own declared properties go beyond that
    /// base set isn't reachable from a <c>RegisterField</c> override today (it only ever receives
    /// <see cref="FieldRenderPayload"/>, not the original <see cref="Component"/> instance) — a
    /// known limitation documented in docs/guides/extending-the-component-catalog.md, not
    /// something this demo needs to work around.
    /// </summary>
    public static void RegisterRendering(GovUkComponentRenderer renderer) =>
        renderer.RegisterField(RatingDiscriminator, RenderRating);

    private static string RenderRating(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var id = field.FieldKey;
        var name = GovUk.FieldName(field.FieldKey);
        var value = field.Value?.ToString() ?? "";
        var hasError = errors.TryGetValue(field.FieldKey, out var error);
        var hasHint = !string.IsNullOrWhiteSpace(field.Hint);

        var hintId = $"{id}-hint";
        var errorId = $"{id}-error";
        var describedByIds = string.Join(' ', new[] { hasHint ? hintId : null, hasError ? errorId : null }.Where(v => v is not null));
        var describedBy = describedByIds.Length == 0 ? "" : $" aria-describedby=\"{describedByIds}\"";

        var hint = hasHint ? $"""<div id="{hintId}" class="govuk-hint">{GovUk.Esc(field.Hint)}</div>""" : "";
        var errorMessage = hasError
            ? $"""<p class="govuk-error-message" id="{errorId}"><span class="govuk-visually-hidden">Error:</span> {GovUk.Esc(error)}</p>"""
            : "";
        var required = field.Required ? "required" : "";

        var items = RatingScale.Select((label, index) =>
        {
            var score = (index + 1).ToString();
            var optionId = $"{id}-{score}";
            var isChecked = string.Equals(score, value, StringComparison.Ordinal);
            return $"""
                <div class="govuk-radios__item">
                  <input class="govuk-radios__input" type="radio" id="{optionId}" name="{name}" value="{score}" {(isChecked ? "checked" : "")} {required}>
                  <label class="govuk-label govuk-radios__label" for="{optionId}">{GovUk.Esc(label)}</label>
                </div>
                """;
        });

        return $"""
            <div class="govuk-form-group{(hasError ? " govuk-form-group--error" : "")}">
              <fieldset class="govuk-fieldset"{describedBy}>
                <legend class="govuk-fieldset__legend govuk-fieldset__legend--m">{GovUk.Esc(field.Label)}</legend>
                {hint}
                {errorMessage}
                <div class="govuk-radios" data-module="govuk-radios">
                  {string.Join("\n", items)}
                </div>
              </fieldset>
            </div>
            """;
    }
}
