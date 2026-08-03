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
    /// A real GOV.UK Design System has no official "stat card" component — this deliberately
    /// avoids Wayfinder.Umbraco's bespoke <c>wayfinder-stat-*</c> CSS this package doesn't ship,
    /// using a plain <c>govuk-heading-l</c>/<c>govuk-body</c> pairing per statistic instead
    /// (the informal pattern several real GOV.UK services use for "big number" displays).
    /// </summary>
    private static string RenderStatGroup(ComponentRenderPayload component)
    {
        var stats = component.Stats ?? Array.Empty<StatItem>();
        var heading = string.IsNullOrEmpty(component.Title) ? "" : $"""<h2 class="govuk-heading-m">{GovUk.Esc(component.Title)}</h2>""";
        var cards = stats.Select(stat =>
        {
            var qualifier = string.IsNullOrEmpty(stat.Qualifier) ? "" : $"""<p class="govuk-body-s">{GovUk.Esc(stat.Qualifier)}</p>""";
            return $"""
                <div class="govuk-grid-column-one-third" data-wayfinder-stat-field="{GovUk.Esc(stat.FieldKey)}">
                  <p class="govuk-body-s govuk-!-margin-bottom-1">{GovUk.Esc(stat.Label)}</p>
                  <p class="govuk-heading-l govuk-!-margin-bottom-1">{GovUk.Esc(string.IsNullOrEmpty(stat.Value) ? "—" : stat.Value)}</p>
                  {qualifier}
                </div>
                """;
        });
        return $"""
            {heading}
            <div class="govuk-grid-row" role="group" aria-label="{GovUk.Esc(component.Title ?? "Key figures")}" aria-live="polite">
              {string.Join("\n", cards)}
            </div>
            """;
    }

    /// <summary>
    /// A real GOV.UK <c>govuk-table</c> — the accessible-data-table representation, always
    /// present. Deliberately doesn't attempt Wayfinder.Umbraco's bespoke bar-chart visualization
    /// (bespoke <c>wayfinder-chart</c> CSS/JS this package doesn't ship); the table alone is a
    /// real, correct GDS component, not a lookalike or a placeholder.
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

        var bands = chart.TryGetProperty("bands", out var bandsElement)
            ? bandsElement.EnumerateArray().Select(band => new
            {
                Key = band.GetProperty("key").GetString() ?? "",
                Label = band.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            }).ToArray()
            : [];

        var xKey = chart.TryGetProperty("x", out var xElement) ? xElement.GetString() ?? "" : "";
        var rows = chart.TryGetProperty("rows", out var rowsElement) ? rowsElement.EnumerateArray().ToArray() : [];

        var headerCells = new[] { $"""<th scope="col">{GovUk.Esc(xKey)}</th>""" }
            .Concat(bands.Select(b => $"""<th scope="col">{GovUk.Esc(b.Label)}</th>"""));

        var bodyRows = rows.Select(row =>
        {
            var xCell = row.TryGetProperty(xKey, out var x) ? x.ToString() : "";
            var valueCells = bands.Select(b => row.TryGetProperty(b.Key, out var v) ? $"""<td class="govuk-table__cell">{GovUk.Esc(v.ToString())}</td>""" : """<td class="govuk-table__cell"></td>""");
            return $"""<tr class="govuk-table__row"><th scope="row" class="govuk-table__header">{GovUk.Esc(xCell)}</th>{string.Join("", valueCells)}</tr>""";
        });

        var caption = string.IsNullOrEmpty(component.Heading) ? "" : $"""<caption class="govuk-table__caption govuk-table__caption--m">{GovUk.Esc(component.Heading)}</caption>""";

        return $"""
            <table class="govuk-table">
              {caption}
              <thead class="govuk-table__head"><tr class="govuk-table__row">{string.Join("", headerCells)}</tr></thead>
              <tbody class="govuk-table__body">{string.Join("\n", bodyRows)}</tbody>
            </table>
            """;
    }

    private static string OptionIdFragment(string value) =>
        string.Concat(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
