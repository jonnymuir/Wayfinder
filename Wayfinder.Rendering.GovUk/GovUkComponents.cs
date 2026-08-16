using System.Text.Json;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// Built-in real <c>govuk-frontend</c> markup for every top-level <c>Component</c> type. Called
/// by <see cref="GovUkComponentRenderer"/>'s dispatch for any type without a host override.
/// </summary>
public static class GovUkComponents
{
    public static string Render(ComponentRenderPayload component, Func<FieldRenderPayload, string> renderField) => component.Type switch
    {
        "fieldset" => RenderFieldset(component, renderField),
        "accordion" => RenderAccordion(component, renderField),
        "summary-list" => RenderSummaryList(component, renderField),
        "panel" => RenderPanel(component),
        "body" => RenderBody(component),
        "heading" => RenderHeading(component),
        "inset-text" => $"""<div class="govuk-inset-text">{component.Content}</div>""",
        "warning-text" => $"""
            <div class="govuk-warning-text">
              <span class="govuk-warning-text__icon" aria-hidden="true">!</span>
              <strong class="govuk-warning-text__text">
                <span class="govuk-visually-hidden">Warning</span>
                {component.Content}
              </strong>
            </div>
            """,
        "details" => $"""
            <details class="govuk-details">
              <summary class="govuk-details__summary">
                <span class="govuk-details__summary-text">{GovUk.Esc(component.Heading)}</span>
              </summary>
              <div class="govuk-details__text">{component.Content}</div>
            </details>
            """,
        "notification-banner" => RenderNotificationBanner(component),
        "waiting" => RenderWaiting(component),
        "task-list" => RenderTaskList(component),
        "stat-group" => RenderStatGroup(component),
        "chart" => RenderChart(component),
        "bulk-data-review" => RenderBulkDataReview(component),
        _ => "",
    };

    private static string RenderFieldset(ComponentRenderPayload component, Func<FieldRenderPayload, string> renderField) => $"""
        <fieldset class="govuk-fieldset">
          {(string.IsNullOrWhiteSpace(component.Legend) ? "" : $"""<legend class="govuk-fieldset__legend govuk-fieldset__legend--{component.LegendSize ?? "m"}">{GovUk.Esc(component.Legend)}</legend>""")}
          {string.Join("\n", component.Fields.Select(renderField))}
        </fieldset>
        """;

    // Deliberately not the overridable renderField delegate — a summary-list row is always a
    // read-only display, regardless of any field's own ReadOnly, same as the original renderer.
    private static string RenderSummaryList(ComponentRenderPayload component, Func<FieldRenderPayload, string> renderField) => $"""
        {(string.IsNullOrWhiteSpace(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{GovUk.Esc(component.Title)}</h2>""")}
        <dl class="govuk-summary-list">
          {string.Join("\n", component.Fields.Select(field => GovUkFields.RenderSummaryRow(field, component.SourceStateKey)))}
        </dl>
        """;

    private static string RenderPanel(ComponentRenderPayload component) => $"""
        <div class="govuk-panel govuk-panel--confirmation">
          <h1 class="govuk-panel__title">{GovUk.Esc(component.Heading)}</h1>
        </div>
        """;

    // Content is already sanitized by the engine before it reaches ComponentRenderPayload.
    private static string RenderBody(ComponentRenderPayload component) => $"""<p class="govuk-body">{component.Content}</p>""";

    private static string RenderHeading(ComponentRenderPayload component) => component.Level switch
    {
        1 => $"""<h1 class="govuk-heading-xl">{GovUk.Esc(component.Content)}</h1>""",
        2 => $"""<h2 class="govuk-heading-l">{GovUk.Esc(component.Content)}</h2>""",
        3 => $"""<h3 class="govuk-heading-m">{GovUk.Esc(component.Content)}</h3>""",
        _ => $"""<h4 class="govuk-heading-s">{GovUk.Esc(component.Content)}</h4>""",
    };

    private static string RenderAccordion(ComponentRenderPayload component, Func<FieldRenderPayload, string> renderField)
    {
        var sections = component.AccordionSections;
        if (sections is null || sections.Count == 0)
        {
            return "";
        }

        var accordionId = $"accordion-{Guid.NewGuid():N}";
        var rendered = sections.Select((section, i) =>
        {
            var sectionId = $"{accordionId}-section-{i + 1}";
            var body = section.Fields is { Count: > 0 }
                ? string.Join("\n", section.Fields.Select(renderField))
                : section.Content ?? "";
            var summary = string.IsNullOrEmpty(section.Summary) ? "" : $"""
                <div class="govuk-accordion__section-summary govuk-body" id="{sectionId}-summary">{GovUk.Esc(section.Summary)}</div>
                """;
            return $"""
                <div class="govuk-accordion__section">
                  <div class="govuk-accordion__section-header">
                    <h2 class="govuk-accordion__section-heading">
                      <span class="govuk-accordion__section-button" id="{sectionId}-heading">{GovUk.Esc(section.Heading)}</span>
                    </h2>
                    {summary}
                  </div>
                  <div id="{sectionId}-content" class="govuk-accordion__section-content" aria-labelledby="{sectionId}-heading">
                    {body}
                  </div>
                </div>
                """;
        });

        return $"""
            <div class="govuk-accordion" data-module="govuk-accordion" id="{accordionId}">
              {string.Join("\n", rendered)}
            </div>
            """;
    }

    private static string RenderNotificationBanner(ComponentRenderPayload component)
    {
        var bannerClass = string.Equals(component.BannerType, "success", StringComparison.OrdinalIgnoreCase)
            ? "govuk-notification-banner govuk-notification-banner--success"
            : "govuk-notification-banner";
        var content = string.IsNullOrEmpty(component.Content) ? "" : $"""
            <div class="govuk-notification-banner__content"><p class="govuk-body">{component.Content}</p></div>
            """;
        return $"""
            <div class="{bannerClass}" role="region" aria-labelledby="wayfinder-banner-title">
              <div class="govuk-notification-banner__header">
                <h2 class="govuk-notification-banner__title" id="wayfinder-banner-title">{GovUk.Esc(component.Heading ?? "Important")}</h2>
              </div>
              {content}
            </div>
            """;
    }

    private static string RenderWaiting(ComponentRenderPayload component)
    {
        // Content is the join gateway's own authored waitingContent (pre-sanitized HTML, per
        // its doc comment — rendered raw, same convention as RenderBody/inset-text/details
        // elsewhere in this file); DeferMessage is a distinct, secondary line only shown when
        // the gateway actually allows deferring, not a substitute for the main message.
        var message = string.IsNullOrEmpty(component.Content)
            ? "This may take a few minutes. You do not need to do anything else right now."
            : component.Content;
        var deferHtml = component.AllowDefer == true && !string.IsNullOrEmpty(component.DeferMessage)
            ? $"""<p class="govuk-body">{GovUk.Esc(component.DeferMessage)}</p>"""
            : "";
        var pollAttr = component.PollIntervalMs is { } poll ? $" data-wayfinder-poll-interval-ms=\"{poll}\"" : "";
        return $"""
            <div class="govuk-inset-text" data-wayfinder-waiting{pollAttr}>
              <p class="govuk-body">{message}</p>
              {deferHtml}
            </div>
            """;
    }

    private static string RenderTaskList(ComponentRenderPayload component)
    {
        var sections = component.TaskSections;
        if (sections is null || sections.Count == 0)
        {
            return """<p class="govuk-body">No tasks available.</p>""";
        }

        var items = sections.SelectMany(section =>
        {
            var header = $"""
                <li class="govuk-task-list__item govuk-task-list__item--header">
                  <span class="govuk-heading-s govuk-!-margin-bottom-0">{GovUk.Esc(section.Heading)}</span>
                </li>
                """;
            var tasks = section.Tasks.Select(task =>
            {
                var (tagClass, tagText) = task.Status.ToLowerInvariant() switch
                {
                    "completed" => ("govuk-tag", task.Status),
                    "in-progress" => ("govuk-tag govuk-tag--blue", task.Status),
                    "cannot-start" => ("govuk-tag govuk-tag--grey", task.Status),
                    _ => ("govuk-tag govuk-tag--grey", "Not started"),
                };
                var itemId = $"task-{OptionIdFragment(task.Label)}";
                var nameAndLink = string.IsNullOrEmpty(task.Href)
                    ? $"""<span class="govuk-task-list__name-and-hint" aria-describedby="{itemId}">{GovUk.Esc(task.Label)}</span>"""
                    : $"""
                        <div class="govuk-task-list__name-and-hint">
                          <a class="govuk-link govuk-task-list__link" href="{GovUk.Esc(task.Href)}" aria-describedby="{itemId}">{GovUk.Esc(task.Label)}</a>
                        </div>
                        """;
                return $"""
                    <li class="govuk-task-list__item govuk-task-list__item--with-link">
                      {nameAndLink}
                      <div class="govuk-task-list__status" id="{itemId}"><strong class="{tagClass}">{GovUk.Esc(tagText)}</strong></div>
                    </li>
                    """;
            });
            return new[] { header }.Concat(tasks);
        });

        return $"""<ul class="govuk-task-list">{string.Join("\n", items)}</ul>""";
    }

    /// <summary>
    /// A real GOV.UK Design System has no official "stat card" component, so this is Wayfinder's
    /// own — <c>wayfinder-stat-*</c>-classed cards a host styles with its own CSS, same as
    /// govuk-frontend's own components need a host to load govuk-frontend's CSS. This is the
    /// gold-standard rendering — hosts don't need their own override for this type.
    /// </summary>
    private static string RenderStatGroup(ComponentRenderPayload component)
    {
        var stats = component.Stats ?? Array.Empty<StatItem>();
        var heading = string.IsNullOrEmpty(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{GovUk.Esc(component.Title)}</h2>""";
        var cards = stats.Select(stat =>
        {
            var qualifier = string.IsNullOrEmpty(stat.Qualifier) ? "" : $"""<div class="wayfinder-stat-card__qualifier">{GovUk.Esc(stat.Qualifier)}</div>""";
            return $"""
                <div class="wayfinder-stat-card{(stat.Emphasis ? " wayfinder-stat-card--emphasis" : "")}" data-wayfinder-stat="{GovUk.Esc(stat.Label)}" data-wayfinder-stat-field="{GovUk.Esc(stat.FieldKey)}">
                  <div class="wayfinder-stat-card__label">{GovUk.Esc(stat.Label)}</div>
                  <div class="wayfinder-stat-card__value">{(string.IsNullOrEmpty(stat.Value) ? "—" : GovUk.Esc(stat.Value))}</div>
                  {qualifier}
                </div>
                """;
        });
        return $"""
            {heading}
            <div class="wayfinder-stat-group" data-wayfinder-stat-group role="group" aria-label="{GovUk.Esc(component.Title ?? "Key figures")}" aria-live="polite">
              {string.Join("\n", cards)}
            </div>
            """;
    }

    /// <summary>
    /// A real GOV.UK Design System has no official chart component, so this is Wayfinder's own —
    /// a <c>wayfinder-chart</c>-classed stacked-bar visualization (progressive-enhancement hook
    /// via <c>data-wayfinder-chart</c>/<c>data-wayfinder-chart-config</c> a host wires its own
    /// JS to, same as govuk-frontend's own components need a host to load govuk-frontend's JS)
    /// plus a genuinely accessible data table always present alongside it, never a substitute for
    /// one. This is the gold-standard rendering — hosts don't need their own override for this type.
    /// </summary>
    private static string RenderChart(ComponentRenderPayload component)
    {
        var chartJson = component.ChartJson;
        if (string.IsNullOrEmpty(chartJson))
        {
            return "";
        }

        using var doc = JsonDocument.Parse(chartJson);
        var chart = doc.RootElement;

        var palette = new[] { "#4f46e5", "#0d9488", "#b45309", "#6d28d9" };
        var bands = chart.TryGetProperty("bands", out var bandsElement)
            ? bandsElement.EnumerateArray().Select((band, index) => new
            {
                Key = band.GetProperty("key").GetString() ?? "",
                Label = band.GetProperty("label").GetString() ?? "",
                Color = band.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String
                    ? color.GetString()!
                    : palette[index % palette.Length]
            }).ToArray()
            : [];

        var xKey = chart.TryGetProperty("x", out var xElement) ? xElement.GetString() ?? "" : "";
        var xLabelEvery = chart.TryGetProperty("xLabelEvery", out var everyElement) ? everyElement.GetInt32() : 5;

        var rows = chart.TryGetProperty("rows", out var rowsElement)
            ? rowsElement.EnumerateArray().Select(row => new
            {
                X = row.GetProperty(xKey).GetDecimal(),
                Values = bands.Select(band => row.GetProperty(band.Key).GetDecimal()).ToArray()
            }).ToArray()
            : [];

        var maxTotal = rows.Length == 0 ? 1m : Math.Max(1m, rows.Max(r => r.Values.Sum()));
        const int plotHeight = 160;
        var safeConfig = chartJson.Replace("</", "<\\/");

        var legend = bands.Select(band => $"""
            <span class="wayfinder-chart__legend-item"><span class="wayfinder-chart__swatch" style="background:{band.Color}"></span>{GovUk.Esc(band.Label)}</span>
            """);

        var bars = rows.Select(row =>
        {
            var segments = string.Join("", Enumerable.Range(0, bands.Length).Select(i =>
                $"""<div style="height:{Math.Round(row.Values[i] / maxTotal * plotHeight, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}px;background:{bands[i].Color}"></div>"""));
            return $"""<div class="wayfinder-chart__bar" title="{GovUk.Esc(xKey)} {row.X}: {row.Values.Sum():N0}">{segments}</div>""";
        });

        var labels = rows.Select(row => $"<span>{(row.X % xLabelEvery == 0 ? row.X.ToString("0") : "")}</span>");

        var headerCells = string.Concat(bands.Select(band => $"""<th scope="col">{GovUk.Esc(band.Label)}</th>"""));
        var tableRows = rows.Where((r, i) => r.X % xLabelEvery == 0 || i == 0).Select(row =>
        {
            var cells = string.Concat(row.Values.Select(v => $"<td>{v:N0}</td>"));
            return $"""<tr><th scope="row">{row.X:0}</th>{cells}</tr>""";
        });

        return $"""
            <figure class="wayfinder-chart" data-wayfinder-chart>
              <script type="application/json" data-wayfinder-chart-config>{safeConfig}</script>
              {(string.IsNullOrEmpty(component.Heading) ? "" : $"""<figcaption class="wayfinder-chart__title">{GovUk.Esc(component.Heading)}</figcaption>""")}
              <div class="wayfinder-chart__legend">{string.Join("\n", legend)}</div>
              <div class="wayfinder-chart__plot" data-wayfinder-chart-plot aria-hidden="true">{string.Join("\n", bars)}</div>
              <div class="wayfinder-chart__labels" data-wayfinder-chart-labels aria-hidden="true">{string.Join("\n", labels)}</div>
              <table class="wayfinder-visually-hidden" data-wayfinder-chart-table>
                <caption>{GovUk.Esc(component.Heading ?? "Chart data")}</caption>
                <thead><tr><th scope="col">{GovUk.Esc(xKey)}</th>{headerCells}</tr></thead>
                <tbody>{string.Join("\n", tableRows)}</tbody>
              </table>
            </figure>
            """;
    }

    /// <summary>
    /// Server-rendered skeleton for the bulk-data review UI (see docs/guides/bulk-data-review.md)
    /// — a summary/controls/rows/pagination shell plus a <c>&lt;noscript&gt;</c> fallback (the
    /// download link always works without JavaScript), progressively enhanced by
    /// wayfinder-bulk-data-review.js via the <c>data-wayfinder-bulk-review-*</c> attributes below.
    /// No row data is rendered server-side: the dataset's own REST endpoints (host-routed — see
    /// <see cref="ComponentRenderPayload.BulkDatasetApiUrl"/>) are what supply it, the same "host
    /// fills in a URL, this package's JS/CSS do the rest" shape <c>wayfinder-chart</c>/
    /// <c>wayfinder-stat-group</c> already use. This is the gold-standard rendering — hosts don't
    /// need their own override for this type.
    /// </summary>
    private static string RenderBulkDataReview(ComponentRenderPayload component)
    {
        var heading = string.IsNullOrEmpty(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{GovUk.Esc(component.Title)}</h2>""";

        if (string.IsNullOrEmpty(component.DatasetId) || string.IsNullOrEmpty(component.BulkDatasetApiUrl))
        {
            return $"""
                {heading}
                <p class="govuk-body">Nothing to review yet.</p>
                """;
        }

        var apiUrl = GovUk.Esc(component.BulkDatasetApiUrl);
        var pageSize = component.PageSize ?? 20;

        return $"""
            {heading}
            <div class="wayfinder-bulk-review" data-wayfinder-bulk-review data-wayfinder-bulk-review-api="{apiUrl}" data-wayfinder-bulk-review-page-size="{pageSize}">
              <noscript>
                <p class="govuk-body">Turn on JavaScript to review rows individually, or <a class="govuk-link" href="{apiUrl}/download">download the full file</a> to review it another way.</p>
              </noscript>
              <div class="wayfinder-bulk-review__summary" data-wayfinder-bulk-review-summary aria-live="polite">
                <p class="govuk-body">Loading…</p>
              </div>
              <div class="wayfinder-bulk-review__controls" data-wayfinder-bulk-review-controls hidden>
                <div class="govuk-button-group">
                  <button type="button" class="govuk-button govuk-button--secondary" data-wayfinder-bulk-review-filter="NeedsAttention" aria-pressed="true">Needs attention</button>
                  <button type="button" class="govuk-button govuk-button--secondary" data-wayfinder-bulk-review-filter="All" aria-pressed="false">All rows</button>
                </div>
                <a class="govuk-link" href="{apiUrl}/download">Download full file</a>
              </div>
              <div class="wayfinder-bulk-review__rows" data-wayfinder-bulk-review-rows></div>
              <nav class="wayfinder-bulk-review__pagination" data-wayfinder-bulk-review-pagination hidden aria-label="Row pages">
                <button type="button" class="govuk-button govuk-button--secondary" data-wayfinder-bulk-review-prev>Previous</button>
                <span data-wayfinder-bulk-review-page-status class="wayfinder-bulk-review__page-status"></span>
                <button type="button" class="govuk-button govuk-button--secondary" data-wayfinder-bulk-review-next>Next</button>
              </nav>
            </div>
            """;
    }

    private static string OptionIdFragment(string value) =>
        string.Concat(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
