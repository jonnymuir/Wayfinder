using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Engine.Worklist;

/// <summary>
/// The GOV.UK-styled worklist markup — filter/sort/search form, the paginated table, status tags,
/// pickup/putback controls — as pure functions of already-resolved data and host-supplied URLs.
/// <see cref="WorklistExtensions.MapWorklist"/> is one caller (the plain-ASP.NET-Core default
/// mountable surface); any other host renders the exact same GOV.UK-correct markup by calling
/// these directly and supplying its own routes/antiforgery token, rather than hand-rolling a
/// second implementation that drifts from this one — see <c>Wayfinder.Umbraco</c>'s own worklist
/// Block Grid component for exactly that host.
/// </summary>
public static class WorklistRenderer
{
    // The exact SVG govuk-frontend's own pagination/template.njk macro emits for the prev/next
    // arrows — copied, not redrawn, so this stays pixel-identical to the real component.
    private const string PaginationPrevIcon =
        """<svg class="govuk-pagination__icon govuk-pagination__icon--prev" xmlns="http://www.w3.org/2000/svg" height="13" width="15" aria-hidden="true" focusable="false" viewBox="0 0 15 13"><path d="m6.5938-0.0078125-6.7266 6.7266 6.7441 6.4062 1.377-1.449-4.1856-3.9768h12.896v-2h-12.984l4.2931-4.293-1.414-1.414z"></path></svg>""";
    private const string PaginationNextIcon =
        """<svg class="govuk-pagination__icon govuk-pagination__icon--next" xmlns="http://www.w3.org/2000/svg" height="13" width="15" aria-hidden="true" focusable="false" viewBox="0 0 15 13"><path d="m8.107-0.0078125-1.4136 1.414 4.2926 4.293h-12.986v2h12.896l-4.1855 3.9766 1.377 1.4492 6.7441-6.4062-6.7246-6.7266z"></path></svg>""";

    /// <summary>
    /// Parses a worklist page's own filter/sort/search/pagination query-string parameters into the
    /// engine call + rendering inputs <see cref="RenderWorklistBody"/> needs. A plain HTML checkbox
    /// form can't distinguish "bare initial load" from "every status box unchecked and submitted"
    /// — both produce zero <paramref name="status"/> values on the wire — so
    /// <paramref name="statusFilterApplied"/> (a hidden field the filter form always posts)
    /// disambiguates: absent means "use the engine's own default status set", present means "take
    /// the (possibly empty) parsed set literally". See docs/guides/queue-worklist-filtering.md.
    /// </summary>
    public static (
        IReadOnlyCollection<QueueWorkItemStatus>? Statuses,
        IReadOnlyCollection<QueueWorkItemStatus> SelectedStatuses,
        QueueWorkListSort Sort,
        int PageIndex,
        int PageSize) ParseWorklistQuery(
        string[]? status, string? sort, int? page, int? pageSize, string? statusFilterApplied, int defaultPageSize)
    {
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = statusFilterApplied is null
            ? null
            : (status ?? [])
                .Select(s => Enum.TryParse<QueueWorkItemStatus>(s, ignoreCase: true, out var parsed) ? (QueueWorkItemStatus?)parsed : null)
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .Distinct()
                .ToArray();
        var selectedStatuses = statuses ?? [QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting, QueueWorkItemStatus.Unassigned];

        var parsedSort = Enum.TryParse<QueueWorkListSort>(sort, ignoreCase: true, out var sortValue)
            ? sortValue
            : QueueWorkListSort.Default;
        var pageIndex = Math.Max(page ?? 0, 0);
        var size = Math.Clamp(pageSize ?? defaultPageSize, 1, 100);

        return (statuses, selectedStatuses, parsedSort, pageIndex, size);
    }

    /// <summary>
    /// The filter/sort/search/paginated-table body for a worklist or team view.
    /// </summary>
    /// <param name="listUrl">
    /// This page's own URL — the filter GET form self-submits here with no explicit "action", and
    /// <c>PageLink</c>'s own href needs it.
    /// </param>
    /// <param name="itemUrlPrefix">
    /// Prefix item review/pickup/putback links and form actions are built from. Always the same
    /// value regardless of which list view rendered the row (a team view's rows still link to the
    /// worklist's own item pages). The review/view link also carries <paramref name="listUrl"/> as
    /// a <c>returnTo</c> query parameter — a host whose item-detail route can't render inline (no
    /// GET body of its own, e.g. one that 302s back into a CMS page render) reads it to know where
    /// "back to worklist" goes; a host that renders the item directly at that route (this package's
    /// own <see cref="WorklistExtensions.MapWorklist"/>) simply ignores the extra parameter.
    /// </param>
    /// <param name="teamNav">
    /// Pre-rendered "My work" / per-team nav markup, or <c>""</c> for a host with no team concept
    /// of its own — this renderer has no opinion on how (or whether) a host resolves teams.
    /// </param>
    /// <param name="antiforgeryToken">
    /// The raw request-verification token value (e.g. <c>IAntiforgery.GetAndStoreTokens(ctx).RequestToken</c>),
    /// not a rendered hidden-input tag — <see cref="RenderPickupPutbackControl"/> builds that tag
    /// itself. <c>null</c>/empty omits the field (a host validating antiforgery a different way).
    /// </param>
    /// <param name="emptyStateMessage">
    /// Shown in place of the table body when nothing matches the current filters. Defaults to this
    /// package's own vocabulary; a host whose domain model uses a different noun for a work item
    /// (Wayfinder.Umbraco's worklist block passes "No service requests match the current filters",
    /// matching its own <c>ServiceRequestWorklistService</c>) supplies its own — found live: adopting
    /// this renderer silently changed that host's displayed copy until a consumer's own Playwright
    /// suite (pinned on the old wording) caught it.
    /// </param>
    public static string RenderWorklistBody(
        string listUrl,
        string itemUrlPrefix,
        string? pageTitle,
        QueueWorkListEnvelope envelope,
        IReadOnlyCollection<QueueWorkItemStatus> selectedStatuses,
        QueueWorkListSort parsedSort,
        string? q,
        int pageIndex,
        int size,
        string teamNav = "",
        string? antiforgeryToken = null,
        string emptyStateMessage = "No applications match the current filters")
    {
        var esc = GovUk.Esc;

        string CheckboxItem(QueueWorkItemStatus value, string label) =>
            $"""
            <div class="govuk-checkboxes__item">
              <input class="govuk-checkboxes__input" id="status-{value}" name="status" type="checkbox" value="{value}" {(selectedStatuses.Contains(value) ? "checked" : "")}>
              <label class="govuk-label govuk-checkboxes__label" for="status-{value}">{label}</label>
            </div>
            """;

        string SortOption(QueueWorkListSort value, string label) =>
            $"""<option value="{value}" {(parsedSort == value ? "selected" : "")}>{label}</option>""";

        // Preserves every other current filter/sort/search choice — only `page` varies — so
        // paging never silently resets a caseworker's status/sort/search selection.
        string PageHref(int targetPageIndex)
        {
            var query = string.Join("&", selectedStatuses.Select(s => $"status={Uri.EscapeDataString(s.ToString())}")
                .Append($"sort={Uri.EscapeDataString(parsedSort.ToString())}")
                .Append(string.IsNullOrWhiteSpace(q) ? null : $"q={Uri.EscapeDataString(q)}")
                .Append($"page={targetPageIndex}")
                .Append($"pageSize={size}")
                .Append("statusFilterApplied=1")
                .Where(part => part is not null));
            return $"{listUrl}?{query}";
        }

        var filterForm = $"""
            <form method="get" class="govuk-!-margin-bottom-6">
              <input type="hidden" name="statusFilterApplied" value="1">
              <div class="govuk-grid-row">
                <div class="govuk-grid-column-one-third">
                  <div class="govuk-form-group">
                    <fieldset class="govuk-fieldset">
                      <legend class="govuk-fieldset__legend govuk-fieldset__legend--s">Status</legend>
                      <div class="govuk-checkboxes govuk-checkboxes--small" data-module="govuk-checkboxes">
                        {CheckboxItem(QueueWorkItemStatus.Actionable, "Actionable")}
                        {CheckboxItem(QueueWorkItemStatus.Unassigned, "Unassigned")}
                        {CheckboxItem(QueueWorkItemStatus.Waiting, "Waiting")}
                        {CheckboxItem(QueueWorkItemStatus.Done, "Done")}
                      </div>
                    </fieldset>
                  </div>
                </div>
                <div class="govuk-grid-column-one-third">
                  <div class="govuk-form-group">
                    <label class="govuk-label" for="q">Search</label>
                    <input class="govuk-input" id="q" name="q" type="search" value="{esc(q ?? "")}">
                  </div>
                </div>
                <div class="govuk-grid-column-one-third">
                  <div class="govuk-form-group">
                    <label class="govuk-label" for="sort">Sort by</label>
                    <select class="govuk-select" id="sort" name="sort">
                      {SortOption(QueueWorkListSort.Default, "Service, then stage")}
                      {SortOption(QueueWorkListSort.UpdatedAtNewestFirst, "Most recently updated")}
                      {SortOption(QueueWorkListSort.UpdatedAtOldestFirst, "Least recently updated")}
                      {SortOption(QueueWorkListSort.CreatedAtNewestFirst, "Newest first")}
                      {SortOption(QueueWorkListSort.CreatedAtOldestFirst, "Oldest first")}
                    </select>
                  </div>
                </div>
              </div>
              <button class="govuk-button govuk-button--secondary" data-module="govuk-button">Apply filters</button>
            </form>
            """;

        string StatusTag(QueueWorkItemStatus itemStatus) => itemStatus switch
        {
            QueueWorkItemStatus.Unassigned => """<strong class="govuk-tag govuk-tag--blue">Unassigned</strong>""",
            QueueWorkItemStatus.Waiting => """<strong class="govuk-tag govuk-tag--yellow">Waiting</strong>""",
            QueueWorkItemStatus.Done => """<strong class="govuk-tag govuk-tag--green">Done</strong>""",
            _ => ""
        };

        var rows = envelope.Items.Count == 0
            ? $"""<tr class="govuk-table__row"><td class="govuk-table__cell" colspan="5">{esc(emptyStateMessage)}</td></tr>"""
            // A waiting item (this caseworker's own cursor parked at a join gateway, waiting on
            // another queue) has nothing to act on yet, but must stay visible and reachable. A
            // done item is genuinely finished, and an unassigned team-tray row hasn't been picked
            // up yet — none of these three can be "reviewed", so they all get a "View" link
            // rather than "Review", making the difference between "you can decide this now" and
            // "nothing (more) to decide (yet)" obvious at a glance.
            : string.Join("\n", envelope.Items.Select(item => $"""
                <tr class="govuk-table__row">
                  <td class="govuk-table__cell">{esc(item.BlueprintDisplayName)}</td>
                  <td class="govuk-table__cell">
                    {esc(item.StateDisplayName)}
                    {StatusTag(item.Status)}
                  </td>
                  <td class="govuk-table__cell">{esc(item.InstanceId[..Math.Min(8, item.InstanceId.Length)])}…</td>
                  <td class="govuk-table__cell"><a class="govuk-link" href="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}?returnTo={Uri.EscapeDataString(listUrl)}">{(item.Status == QueueWorkItemStatus.Actionable ? "Review" : "View")}</a></td>
                  <td class="govuk-table__cell">{RenderPickupPutbackControl(item, itemUrlPrefix, listUrl, antiforgeryToken)}</td>
                </tr>
                """));

        // The real GOV.UK Pagination component, "block" variant (previous/next only — no page-
        // number list, since a worklist doesn't know its own total page count up front any more
        // meaningfully than "keep clicking Next"). Matches the govuk-frontend template.njk macro
        // exactly, including omitting a direction's whole <div> rather than rendering it disabled
        // when there's no page that way — see Wayfinder.Rendering.GovUk's own bulk-data-review
        // pagination for the same pattern already used elsewhere in this codebase.
        var hasNextPage = (pageIndex + 1) * size < envelope.TotalMatchingCount;
        var prevLink = pageIndex > 0
            ? $"""
            <div class="govuk-pagination__prev">
              <a class="govuk-link govuk-pagination__link" href="{PageHref(pageIndex - 1)}" rel="prev">
                {PaginationPrevIcon}
                <span class="govuk-pagination__link-title govuk-pagination__link-title--decorated">Previous<span class="govuk-visually-hidden"> page</span></span>
              </a>
            </div>
            """
            : "";
        var nextLink = hasNextPage
            ? $"""
            <div class="govuk-pagination__next">
              <a class="govuk-link govuk-pagination__link" href="{PageHref(pageIndex + 1)}" rel="next">
                <span class="govuk-pagination__link-title govuk-pagination__link-title--decorated">Next<span class="govuk-visually-hidden"> page</span></span>
                {PaginationNextIcon}
              </a>
            </div>
            """
            : "";
        var pagination = envelope.TotalMatchingCount == 0
            ? ""
            : $"""
            <nav class="govuk-pagination govuk-pagination--block govuk-!-margin-top-4" aria-label="Worklist pages">
              {prevLink}
              {nextLink}
            </nav>
            <p class="govuk-body">Page {pageIndex + 1} — showing {envelope.Items.Count} of {envelope.TotalMatchingCount}</p>
            """;

        var heading = string.IsNullOrEmpty(pageTitle) ? "" : $"""<h1 class="govuk-heading-xl">{esc(pageTitle)}</h1>""";

        return $"""
            {heading}
            {teamNav}
            {filterForm}
            <table class="govuk-table">
              <thead class="govuk-table__head">
                <tr class="govuk-table__row">
                  <th class="govuk-table__header" scope="col">Service</th>
                  <th class="govuk-table__header" scope="col">Stage</th>
                  <th class="govuk-table__header" scope="col">Instance</th>
                  <th class="govuk-table__header" scope="col"><span class="govuk-visually-hidden">Actions</span></th>
                  <th class="govuk-table__header" scope="col"><span class="govuk-visually-hidden">Pick up</span></th>
                </tr>
              </thead>
              <tbody class="govuk-table__body">{rows}</tbody>
            </table>
            {pagination}
            """;
    }

    /// <summary>
    /// See docs/guides/work-allocation.md — pickup/ownership is per-cursor (or, for a team-owned
    /// queue, per-<c>QueueAssignment</c>), orthogonal to <see cref="QueueWorkItemStatus"/>. Rendered
    /// as "pick up"/"put back" — plain English, matching what the engine's own API calls it too
    /// (<see cref="Abstractions.IProcessManager.PickupWorkItem"/>/<see cref="Abstractions.IProcessManager.PutbackWorkItem"/>),
    /// so there's one vocabulary from the button a caseworker clicks through to the audit log.
    /// </summary>
    /// <param name="returnTo">
    /// Where the pickup/putback POST should PRG back to — a hidden "returnTo" form field, read by
    /// whatever endpoint handles the post. The host's own job to honour (see
    /// <see cref="WorklistExtensions"/>'s own <c>ResolveReturnTo</c> for the plain-ASP.NET-Core
    /// default); this renderer just carries the value through.
    /// </param>
    /// <param name="antiforgeryToken">
    /// The raw request-verification token value, or <c>null</c>/empty to omit the field.
    /// </param>
    public static string RenderPickupPutbackControl(
        QueueWorkItem item, string itemUrlPrefix, string returnTo, string? antiforgeryToken) => item.PickupState switch
    {
        QueueWorkItemPickupState.NotPickedUp => $"""
            <form method="post" action="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}/pickup?cursorId={Uri.EscapeDataString(item.CursorId)}">
              <input type="hidden" name="returnTo" value="{GovUk.Esc(returnTo)}">{(string.IsNullOrEmpty(antiforgeryToken) ? "" : $"""<input type="hidden" name="__RequestVerificationToken" value="{GovUk.Esc(antiforgeryToken)}">""")}
              <button class="govuk-button govuk-button--secondary govuk-!-margin-0" data-module="govuk-button">Pick up</button>
            </form>
            """,
        QueueWorkItemPickupState.PickedUpByMe => $"""
            <strong class="govuk-tag">With you</strong>
            <form method="post" class="govuk-!-margin-top-2" action="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}/putback?cursorId={Uri.EscapeDataString(item.CursorId)}">
              <input type="hidden" name="returnTo" value="{GovUk.Esc(returnTo)}">{(string.IsNullOrEmpty(antiforgeryToken) ? "" : $"""<input type="hidden" name="__RequestVerificationToken" value="{GovUk.Esc(antiforgeryToken)}">""")}
              <button class="govuk-button govuk-button--secondary govuk-!-margin-0" data-module="govuk-button">Put back</button>
            </form>
            """,
        _ => ""
    };
}
