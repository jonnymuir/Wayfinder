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
            var tenantId = options.ResolveTenantId!(ctx);
            var userId = options.ResolveUserId(ctx);
            var accessProfile = options.ResolveAccessProfile!(ctx);

            var (statuses, selectedStatuses, parsedSort, pageIndex, size) =
                WorklistRenderer.ParseWorklistQuery(status, sort, page, pageSize, statusFilterApplied, options.DefaultPageSize);

            var envelope = engine.GetQueueWorkItems(tenantId, userId, accessProfile, statuses, parsedSort, q, pageIndex, size);

            var body = WorklistRenderer.RenderWorklistBody(
                prefix, prefix, options.WorklistPageTitle, envelope, selectedStatuses, parsedSort, q, pageIndex, size,
                RenderTeamNav(prefix, ctx, options, currentTeamId: null),
                WayfinderAntiforgery.MintRequestVerificationToken(ctx));

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
                WorklistRenderer.ParseWorklistQuery(status, sort, page, pageSize, statusFilterApplied, options.DefaultPageSize);

            var envelope = engine.GetTeamWorkItems(tenantId, teamId, accessProfile, statuses, parsedSort, q, pageIndex, size);

            var teamPrefix = $"{prefix}/team/{Uri.EscapeDataString(teamId)}";
            var body = WorklistRenderer.RenderWorklistBody(
                teamPrefix, prefix, options.TeamWorklistPageTitle, envelope, selectedStatuses, parsedSort, q, pageIndex, size,
                RenderTeamNav(prefix, ctx, options, currentTeamId: teamId),
                WayfinderAntiforgery.MintRequestVerificationToken(ctx));

            return Results.Content(options.RenderPage!(options.TeamWorklistPageTitle, body, ctx), "text/html");
        });

        // Pickup/putback — see docs/guides/work-allocation.md. PRG back to wherever the pickup was
        // initiated from (the personal worklist, or a team view — see PickupPutbackControl's own
        // hidden "returnTo" field), defaulting to the personal worklist if that's missing or looks
        // unsafe. The query-string cursorId (not a route segment) matches how QueueWorkItem.CursorId
        // is already surfaced to the worklist's own Pickup/Putback form actions above.
        group.MapPost("/{blueprintKey}/{instanceId}/pickup", async (
            string blueprintKey, string instanceId, string cursorId, HttpContext ctx,
            IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            engine.PickupWorkItem(instanceId, cursorId, tenantId, userId, options.ResolveAccessProfile!(ctx));
            return Results.Redirect(await ResolveReturnTo(ctx, prefix));
        });

        group.MapPost("/{blueprintKey}/{instanceId}/putback", async (
            string blueprintKey, string instanceId, string cursorId, HttpContext ctx,
            IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            engine.PutbackWorkItem(instanceId, cursorId, tenantId, userId, options.ResolveAccessProfile!(ctx));
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
            var antiforgeryToken = WayfinderAntiforgery.MintRequestVerificationToken(ctx);
            envelope = envelope.WithFileDownloadUrls($"{prefix}/{blueprintKey}/{instanceId}/files");
            envelope = envelope.WithBulkDatasetApiUrls($"{prefix}/{blueprintKey}/{instanceId}/bulk-datasets", antiforgeryToken);
            return Results.Content(
                options.RenderPage!(
                    options.ReviewPageTitle,
                    renderer.RenderJourneyBody(envelope, $"{prefix}/{blueprintKey}/{instanceId}/advance", antiforgeryToken),
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
            var antiforgeryToken = WayfinderAntiforgery.MintRequestVerificationToken(ctx);
            var action = form["action"].ToString();
            var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
            var fieldValues = GovUkStageJourney.CoerceFieldValues(form, current.Render);

            var fileErrors = await StageFileUploads.ApplyFileUploadsAsync(form, current.Render, instanceId, fileStorage, fieldValues);
            if (fileErrors.Count > 0)
            {
                return Results.Content(
                    options.RenderPage!(options.ReviewPageTitle, renderer.RenderJourneyBody(current with { Problems = fileErrors }, $"{prefix}/{blueprintKey}/{instanceId}/advance", antiforgeryToken), ctx), "text/html");
            }

            var result = engine.Advance(instanceId, tenantId, userId, profile, action, stateVersion, fieldValues);

            if (result.Problems.Count > 0 && result.Render is not null)
            {
                return Results.Content(
                    options.RenderPage!(options.ReviewPageTitle, renderer.RenderJourneyBody(result, $"{prefix}/{blueprintKey}/{instanceId}/advance", antiforgeryToken), ctx), "text/html");
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
            HttpContext ctx, string blueprintKey, string instanceId, string fieldKey,
            IProcessManager engine, IServiceRequestFileStorage fileStorage, IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var tenantId = options.ResolveTenantId!(ctx);
            var userId = options.ResolveUserId(ctx);
            var accessProfile = options.ResolveAccessProfile!(ctx);

            var rawValues = engine.TryGetAccessibleInstance(instanceId, tenantId, userId, accessProfile)?.FieldValues;
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


    /// <summary>
    /// Reads the "returnTo" hidden field a pickup/putback form posted (see
    /// <see cref="WorklistRenderer.RenderPickupPutbackControl"/>) — only trusted when it's a genuinely local,
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
}
