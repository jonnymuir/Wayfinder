using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// A real GOV.UK Design System has no official "stat card" component, so this is Wayfinder's
/// own: <c>wayfinder-stat-*</c>-classed cards a host styles with its own CSS, same as
/// govuk-frontend's own components need a host to load govuk-frontend's CSS. This is the
/// gold-standard rendering, so hosts don't need their own override for this type.
/// </summary>
internal static class GovUkStatGroup
{
    internal static string Render(ComponentRenderPayload component)
    {
        var stats = component.Stats ?? Array.Empty<StatItem>();
        var heading = string.IsNullOrEmpty(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{GovUk.Esc(component.Title)}</h2>""";
        return $"""
            {heading}
            <div class="wayfinder-stat-group" data-wayfinder-stat-group role="group" aria-label="{GovUk.Esc(component.Title ?? "Key figures")}" aria-live="polite">
              {string.Join("\n", stats.Select(RenderCard))}
            </div>
            """;
    }

    private static string RenderCard(StatItem stat)
    {
        var qualifier = string.IsNullOrEmpty(stat.Qualifier) ? "" : $"""<div class="wayfinder-stat-card__qualifier">{GovUk.Esc(stat.Qualifier)}</div>""";
        var hasValue = !string.IsNullOrEmpty(stat.Value);

        // The point is always written out as text too, so the tile reads without the script and to a screen
        // reader; the script adds the map beneath it, and removes it again if the value is not a point.
        var isMap = hasValue && string.Equals(stat.Display, "map", StringComparison.Ordinal);
        var classes = "wayfinder-stat-card" + (stat.Emphasis ? " wayfinder-stat-card--emphasis" : "") + (isMap ? " wayfinder-stat-card--map" : "")
            + (string.Equals(stat.Width, "full", StringComparison.Ordinal) ? " wayfinder-stat-card--full" : "");
        var map = isMap
            ? $"""<div class="wayfinder-location-view" data-wayfinder-location-map data-wayfinder-location="{GovUk.Esc(stat.Value)}" data-wayfinder-label="{GovUk.Esc(stat.Label)}"></div>"""
            : "";
        return $"""
            <div class="{classes}" data-wayfinder-stat="{GovUk.Esc(stat.Label)}" data-wayfinder-stat-field="{GovUk.Esc(stat.FieldKey)}">
              <div class="wayfinder-stat-card__label">{GovUk.Esc(stat.Label)}</div>
              <div class="wayfinder-stat-card__value{(isMap ? " wayfinder-stat-card__value--point" : "")}">{(hasValue ? GovUk.Esc(stat.Value) : "—")}</div>
              {map}
              {qualifier}
            </div>
            """;
    }
}
