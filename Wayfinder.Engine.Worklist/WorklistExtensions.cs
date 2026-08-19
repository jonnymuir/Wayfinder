using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Http;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Engine.Worklist;

/// <summary>
/// Registers and maps the default caseworker worklist surface — see this package's own README
/// and docs/guides/work-allocation.md / docs/guides/queue-worklist-filtering.md / docs/guides/team-assignment.md.
/// Ported verbatim from Wayfinder.ReferenceApp/Program.cs's own hand-written caseworker routes,
/// with one real fix: every redirect/link/form-action is now built from <c>prefix</c> rather than
/// a hardcoded "/caseworker/queue" string, so <see cref="MapWorklist"/> genuinely supports being
/// mounted anywhere a host likes.
/// </summary>
public static class WorklistExtensions
{
    public static IServiceCollection AddWorklist(this IServiceCollection services, Action<WorklistOptions> configure)
    {
        services.AddOptions<WorklistOptions>()
            .Configure(configure)
            .Validate(o => o.ResolveTenantId is not null, $"{nameof(WorklistOptions.ResolveTenantId)} must be set.")
            .Validate(o => o.ResolveAccessProfile is not null, $"{nameof(WorklistOptions.ResolveAccessProfile)} must be set.")
            .Validate(o => o.RenderPage is not null, $"{nameof(WorklistOptions.RenderPage)} must be set.")
            .ValidateOnStart();
        return services;
    }

    public static RouteGroupBuilder MapWorklist(this IEndpointRouteBuilder endpoints, string prefix = "/wayfinder/worklist")
    {
        var group = endpoints.MapGroup(prefix);

        // Filter/sort/search/pagination controls for the worklist (see
        // docs/guides/queue-worklist-filtering.md) — a real <form method="get">, full-page reload.
        // A plain HTML checkbox form can't distinguish "bare initial load" from "every status box
        // unchecked and submitted" — both produce zero `status` values on the wire — so a hidden
        // `statusFilterApplied` field disambiguates: absent means "use GetQueueWorkItems' own
        // default", present means "take the (possibly empty) parsed set literally".
        group.MapGet("", (
            HttpContext ctx, IProcessManager engine, IOptions<WorklistOptions> optionsAccessor,
            string[]? status, string? sort, string? q, int? page, int? pageSize, string? statusFilterApplied) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var accessProfile = options.ResolveAccessProfile!(ctx);

            var (statuses, selectedStatuses, parsedSort, pageIndex, size) =
                ParseWorklistQuery(status, sort, page, pageSize, statusFilterApplied, options.DefaultPageSize);

            var envelope = engine.GetQueueWorkItems(userId, accessProfile, statuses, parsedSort, q, pageIndex, size);

            var body = RenderWorklistBody(
                prefix, prefix, options.WorklistPageTitle, envelope, selectedStatuses, parsedSort, q, pageIndex, size,
                RenderTeamNav(prefix, ctx, options, currentTeamId: null));

            return Results.Content(options.RenderPage!(options.WorklistPageTitle, body, ctx), "text/html");
        });

        // A team's own aggregate view of everything it owns — see
        // docs/guides/team-assignment.md and IProcessManager.GetTeamWorkItems's own remarks.
        // Only mapped when a host actually wants it; WorklistOptions.ResolveTeams staying unset is
        // a fully supported "this host has no team-owned queues" shape, not a required option.
        group.MapGet("/team/{teamId}", (
            string teamId, HttpContext ctx, IProcessManager engine, IOptions<WorklistOptions> optionsAccessor,
            string[]? status, string? sort, string? q, int? page, int? pageSize, string? statusFilterApplied) =>
        {
            var options = optionsAccessor.Value;
            var accessProfile = options.ResolveAccessProfile!(ctx);
            var tenantId = options.ResolveTenantId!(ctx);

            var (statuses, selectedStatuses, parsedSort, pageIndex, size) =
                ParseWorklistQuery(status, sort, page, pageSize, statusFilterApplied, options.DefaultPageSize);

            var envelope = engine.GetTeamWorkItems(tenantId, teamId, accessProfile, statuses, parsedSort, q, pageIndex, size);

            var teamPrefix = $"{prefix}/team/{Uri.EscapeDataString(teamId)}";
            var body = RenderWorklistBody(
                teamPrefix, prefix, options.TeamWorklistPageTitle, envelope, selectedStatuses, parsedSort, q, pageIndex, size,
                RenderTeamNav(prefix, ctx, options, currentTeamId: teamId));

            return Results.Content(options.RenderPage!(options.TeamWorklistPageTitle, body, ctx), "text/html");
        });

        // Claim/release — see docs/guides/work-allocation.md. PRG back to wherever the claim was
        // initiated from (the personal worklist, or a team view — see ClaimReleaseControl's own
        // hidden "returnTo" field), defaulting to the personal worklist if that's missing or looks
        // unsafe. The query-string cursorId (not a route segment) matches how QueueWorkItem.CursorId
        // is already surfaced to the worklist's own Claim/Release form actions above.
        group.MapPost("/{blueprintKey}/{instanceId}/claim", async (
            string blueprintKey, string instanceId, string cursorId, HttpContext ctx,
            IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            engine.ClaimWorkItem(instanceId, cursorId, tenantId, userId, options.ResolveAccessProfile!(ctx));
            return Results.Redirect(await ResolveReturnTo(ctx, prefix));
        });

        group.MapPost("/{blueprintKey}/{instanceId}/release", async (
            string blueprintKey, string instanceId, string cursorId, HttpContext ctx,
            IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            engine.ReleaseWorkItem(instanceId, cursorId, tenantId, userId, options.ResolveAccessProfile!(ctx));
            return Results.Redirect(await ResolveReturnTo(ctx, prefix));
        });

        group.MapGet("/{blueprintKey}/{instanceId}", (
            string blueprintKey, string instanceId, HttpContext ctx,
            IProcessManager engine, GovUkComponentRenderer renderer, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var envelope = engine.GetCurrent(
                blueprintKey, options.ResolveTenantId!(ctx), userId, options.ResolveAccessProfile!(ctx), instanceId);
            envelope = envelope.WithFileDownloadUrls($"{prefix}/{blueprintKey}/{instanceId}/files");
            envelope = envelope.WithBulkDatasetApiUrls($"{prefix}/{blueprintKey}/{instanceId}/bulk-datasets");
            return Results.Content(
                options.RenderPage!(
                    options.ReviewPageTitle,
                    renderer.RenderJourneyBody(envelope, $"{prefix}/{blueprintKey}/{instanceId}/advance"),
                    ctx),
                "text/html");
        });

        group.MapPost("/{blueprintKey}/{instanceId}/advance", async (
            string blueprintKey, string instanceId, HttpContext ctx,
            IProcessManager engine, GovUkComponentRenderer renderer, IServiceRequestFileStorage fileStorage,
            IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            var profile = options.ResolveAccessProfile!(ctx);
            var current = engine.GetCurrent(blueprintKey, tenantId, userId, profile, instanceId);

            var form = await ctx.Request.ReadFormAsync();
            var action = form["action"].ToString();
            var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
            var fieldValues = GovUkStageJourney.CoerceFieldValues(form, current.Render);

            var fileErrors = await StageFileUploads.ApplyFileUploadsAsync(form, current.Render, instanceId, fileStorage, fieldValues);
            if (fileErrors.Count > 0)
            {
                return Results.Content(
                    options.RenderPage!(options.ReviewPageTitle, renderer.RenderJourneyBody(current with { Problems = fileErrors }, $"{prefix}/{blueprintKey}/{instanceId}/advance"), ctx), "text/html");
            }

            var result = engine.Advance(instanceId, tenantId, userId, profile, action, stateVersion, fieldValues);

            if (result.Problems.Count > 0 && result.Render is not null)
            {
                return Results.Content(
                    options.RenderPage!(options.ReviewPageTitle, renderer.RenderJourneyBody(result, $"{prefix}/{blueprintKey}/{instanceId}/advance"), ctx), "text/html");
            }

            // PRG, but back to whichever place actually has the caseworker's next move: advancing
            // to a non-terminal stage stays on the item page; advancing to a terminal decision or
            // into a wait (defer) goes back to the queue.
            var next = engine.GetCurrent(blueprintKey, tenantId, userId, profile, instanceId);
            var hasMoreToDoHere = next.Render?.AvailableActions.Count > 0 || next.ResponseState == "defer";

            return Results.Redirect(hasMoreToDoHere
                ? $"{prefix}/{blueprintKey}/{instanceId}"
                : prefix);
        });

        // A caseworker reviewing an application needs to actually open what was uploaded, not just
        // read its filename — see GovUkStageJourney.WithFileDownloadUrls, which already builds
        // exactly this URL for every file-upload field on the item page above.
        group.MapGet("/{blueprintKey}/{instanceId}/files/{fieldKey}", async (
            string blueprintKey, string instanceId, string fieldKey, IProcessManager engine, IServiceRequestFileStorage fileStorage) =>
        {
            var rawValues = engine.GetAllInstances().FirstOrDefault(request => request.InstanceId == instanceId)?.FieldValues;
            var reference = rawValues is null ? null : ServiceRequestFileReference.FromFieldValue(rawValues.GetValueOrDefault(fieldKey));
            if (reference is null)
            {
                return Results.NotFound();
            }

            var stream = await fileStorage.OpenReadAsync(reference.StorageKey);
            if (stream is null)
            {
                return Results.NotFound();
            }

            var contentType = string.IsNullOrEmpty(reference.ContentType) ? "application/octet-stream" : reference.ContentType;
            return Results.File(stream, contentType, reference.OriginalFileName);
        });

        return group;
    }

    private static (
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
    /// Reads the "returnTo" hidden field a claim/release form posted (see
    /// <see cref="RenderClaimReleaseControl"/>) — only trusted when it's a genuinely local,
    /// relative path (starts with "/", never "//" — the same open-redirect guard
    /// Program.cs's own login flow already uses), falling back to <paramref name="fallback"/>
    /// (the personal worklist) otherwise.
    /// </summary>
    private static async Task<string> ResolveReturnTo(HttpContext ctx, string fallback)
    {
        var form = await ctx.Request.ReadFormAsync();
        var returnTo = form["returnTo"].ToString();
        return !string.IsNullOrWhiteSpace(returnTo) && returnTo.StartsWith('/') && !returnTo.StartsWith("//", StringComparison.Ordinal)
            ? returnTo
            : fallback;
    }

    /// <summary>
    /// A small "My work" / one link per team nav, rendered at the top of both the personal
    /// worklist and any team view — only when <see cref="WorklistOptions.ResolveTeams"/> is set.
    /// See docs/guides/team-assignment.md.
    /// </summary>
    private static string RenderTeamNav(string prefix, HttpContext ctx, WorklistOptions options, string? currentTeamId)
    {
        if (options.ResolveTeams is null)
        {
            return "";
        }

        var esc = GovUk.Esc;
        var teams = options.ResolveTeams(ctx);
        if (teams.Count == 0)
        {
            return "";
        }

        string NavLink(string href, string label, bool current) =>
            current
                ? $"""<strong class="govuk-!-margin-right-4">{esc(label)}</strong>"""
                : $"""<a class="govuk-link govuk-!-margin-right-4" href="{href}">{esc(label)}</a>""";

        var teamLinks = teams.Select(team => NavLink(
            $"{prefix}/team/{Uri.EscapeDataString(team.TeamId)}", team.DisplayName,
            string.Equals(team.TeamId, currentTeamId, StringComparison.Ordinal)));

        // govuk-frontend never sets font-family on body/html globally — only per typography class
        // (.govuk-body, .govuk-link, .govuk-heading-*, ...). The "current page" label below is a
        // bare <strong> with no typography class of its own, so without govuk-body here to cascade
        // font-family down to it, it silently falls back to the browser's serif default — found
        // live (rendered as Times New Roman). The <a class="govuk-link"> siblings already carry
        // their own font-family regardless, so this is belt-and-braces for them, not a fix.
        return $"""
            <nav class="govuk-body govuk-!-margin-bottom-4">
              {NavLink(prefix, "My work", currentTeamId is null)}
              {string.Join("\n", teamLinks)}
            </nav>
            """;
    }

    /// <summary>
    /// The shared filter/sort/search/paginated-table body for both the personal worklist and a
    /// team view — see docs/guides/queue-worklist-filtering.md / docs/guides/team-assignment.md.
    /// <paramref name="listUrl"/> is this page's own URL (the GET filter form self-submits here
    /// with no explicit "action", but <c>PageLink</c>'s own href needs it); <paramref name="itemUrlPrefix"/>
    /// is always the worklist's own <c>prefix</c> — item review/claim/release links always point
    /// there regardless of which list view rendered them.
    /// </summary>
    private static string RenderWorklistBody(
        string listUrl,
        string itemUrlPrefix,
        string pageTitle,
        QueueWorkListEnvelope envelope,
        IReadOnlyCollection<QueueWorkItemStatus> selectedStatuses,
        QueueWorkListSort parsedSort,
        string? q,
        int pageIndex,
        int size,
        string teamNav)
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
        string PageLink(int targetPageIndex, string label)
        {
            var query = string.Join("&", selectedStatuses.Select(s => $"status={Uri.EscapeDataString(s.ToString())}")
                .Append($"sort={Uri.EscapeDataString(parsedSort.ToString())}")
                .Append(string.IsNullOrWhiteSpace(q) ? null : $"q={Uri.EscapeDataString(q)}")
                .Append($"page={targetPageIndex}")
                .Append($"pageSize={size}")
                .Append("statusFilterApplied=1")
                .Where(part => part is not null));
            return $"""<a class="govuk-link" href="{listUrl}?{query}">{label}</a>""";
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
            ? """<tr class="govuk-table__row"><td class="govuk-table__cell" colspan="5">No applications match the current filters</td></tr>"""
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
                  <td class="govuk-table__cell"><a class="govuk-link" href="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}">{(item.Status == QueueWorkItemStatus.Actionable ? "Review" : "View")}</a></td>
                  <td class="govuk-table__cell">{RenderClaimReleaseControl(item, itemUrlPrefix, listUrl)}</td>
                </tr>
                """));

        var hasNextPage = (pageIndex + 1) * size < envelope.TotalMatchingCount;
        var pagination = envelope.TotalMatchingCount == 0
            ? ""
            : $"""
            <nav class="govuk-!-margin-top-4">
              {(pageIndex > 0 ? PageLink(pageIndex - 1, "Previous") : """<span class="govuk-body">Previous</span>""")}
              <span class="govuk-body">Page {pageIndex + 1} — showing {envelope.Items.Count} of {envelope.TotalMatchingCount}</span>
              {(hasNextPage ? PageLink(pageIndex + 1, "Next") : """<span class="govuk-body">Next</span>""")}
            </nav>
            """;

        return $"""
            <h1 class="govuk-heading-xl">{esc(pageTitle)}</h1>
            {teamNav}
            {filterForm}
            <table class="govuk-table">
              <thead class="govuk-table__head">
                <tr class="govuk-table__row">
                  <th class="govuk-table__header" scope="col">Service</th>
                  <th class="govuk-table__header" scope="col">Stage</th>
                  <th class="govuk-table__header" scope="col">Instance</th>
                  <th class="govuk-table__header" scope="col"><span class="govuk-visually-hidden">Actions</span></th>
                  <th class="govuk-table__header" scope="col"><span class="govuk-visually-hidden">Claim</span></th>
                </tr>
              </thead>
              <tbody class="govuk-table__body">{rows}</tbody>
            </table>
            {pagination}
            """;
    }

    /// <summary>
    /// See docs/guides/work-allocation.md — claim/ownership is per-cursor (or, for a team-owned
    /// queue, per-<c>QueueAssignment</c>), orthogonal to <see cref="QueueWorkItemStatus"/>. A
    /// Claim/Release button posts back to this same page (PRG, via the hidden "returnTo" field —
    /// see <see cref="ResolveReturnTo"/>), so claiming never leaves a caseworker mid-way through a
    /// stale filtered view, and never bounces someone from a team view back to their personal one.
    /// </summary>
    private static string RenderClaimReleaseControl(QueueWorkItem item, string itemUrlPrefix, string returnTo) => item.ClaimState switch
    {
        QueueWorkItemClaimState.Unclaimed => $"""
            <form method="post" action="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}/claim?cursorId={Uri.EscapeDataString(item.CursorId)}">
              <input type="hidden" name="returnTo" value="{GovUk.Esc(returnTo)}">
              <button class="govuk-button govuk-button--secondary govuk-!-margin-0" data-module="govuk-button">Claim</button>
            </form>
            """,
        QueueWorkItemClaimState.ClaimedByMe => $"""
            <strong class="govuk-tag">Claimed by you</strong>
            <form method="post" action="{itemUrlPrefix}/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}/release?cursorId={Uri.EscapeDataString(item.CursorId)}">
              <input type="hidden" name="returnTo" value="{GovUk.Esc(returnTo)}">
              <button class="govuk-button govuk-button--secondary govuk-!-margin-0" data-module="govuk-button">Release</button>
            </form>
            """,
        _ => ""
    };
}
