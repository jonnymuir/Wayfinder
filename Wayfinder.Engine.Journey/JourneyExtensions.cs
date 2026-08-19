using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Http;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Engine.Journey;

/// <summary>
/// Registers and maps the default single-actor journey surface — see this package's own README.
/// Ported verbatim from Wayfinder.ReferenceApp/Program.cs's own hand-written "/apply" and
/// "/premium" route pairs, which were near-identical apart from their blueprint key, prefix, and
/// page title — now the same one <see cref="MapJourney"/> call, parameterized by those three.
/// </summary>
public static class JourneyExtensions
{
    public static IServiceCollection AddJourney(this IServiceCollection services, Action<JourneyOptions> configure)
    {
        services.AddOptions<JourneyOptions>()
            .Configure(configure)
            .Validate(o => o.ResolveTenantId is not null, $"{nameof(JourneyOptions.ResolveTenantId)} must be set.")
            .Validate(o => o.ResolveAccessProfile is not null, $"{nameof(JourneyOptions.ResolveAccessProfile)} must be set.")
            .Validate(o => o.RenderPage is not null, $"{nameof(JourneyOptions.RenderPage)} must be set.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Maps GET and POST at the same <paramref name="prefix"/> — GET renders the actor's current
    /// stage of <paramref name="blueprintKey"/>; POST coerces the posted form, applies any file
    /// uploads, advances, and redirects back to <paramref name="prefix"/> (POST-redirect-GET, so a
    /// reload or a second tab never resubmits a stale <c>stateVersion</c>). A rejected submission
    /// (validation problems) renders directly instead of redirecting — state didn't change, so
    /// there's nothing stale to protect against.
    ///
    /// GET is deliberately ambient <c>GetCurrent</c>, not <c>GetCurrentOrStartFresh</c> — an
    /// ordinary visit to this same link must keep showing a just-reached terminal stage forever
    /// (a returning citizen sees "Thank you", not a silently-reset blank form; existing tests
    /// depend on a reload behaving exactly this way). What was missing is the *other* half:
    /// there was no way to explicitly start a new one at all once terminal — <c>{prefix}/new</c>
    /// below is that distinct affordance (see <c>IProcessManager.GetCurrentOrStartFresh</c>'s own
    /// remarks), and GET itself offers a link to it automatically whenever the current response
    /// is genuinely terminal, so a host using this package gets it for free without authoring any
    /// blueprint content for it.
    /// </summary>
    public static RouteGroupBuilder MapJourney(
        this IEndpointRouteBuilder endpoints, string prefix, string blueprintKey, string pageTitle,
        string startNewLabel = "Start a new one")
    {
        var group = endpoints.MapGroup(prefix);

        group.MapGet("", (HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer, IOptions<JourneyOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var envelope = engine.GetCurrent(
                blueprintKey, options.ResolveTenantId!(ctx), options.ResolveUserId(ctx), options.ResolveAccessProfile!(ctx));
            var body = renderer.RenderJourneyBody(envelope, prefix);
            if (envelope.ResponseState == "complete")
            {
                body += $"""<p class="govuk-body"><a class="govuk-link" href="{prefix}/new">{GovUk.Esc(startNewLabel)}</a></p>""";
            }
            return Results.Content(options.RenderPage!(pageTitle, body, ctx), "text/html");
        });

        // The distinct "start a new one" affordance — see this method's own remarks above and
        // IProcessManager.GetCurrentOrStartFresh's. Reinstates a still-in-progress instance rather
        // than abandoning it; only actually starts fresh once the existing one is genuinely
        // terminal.
        group.MapGet("/new", (HttpContext ctx, IProcessManager engine, IOptions<JourneyOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            engine.GetCurrentOrStartFresh(
                blueprintKey, options.ResolveTenantId!(ctx), options.ResolveUserId(ctx), options.ResolveAccessProfile!(ctx));
            return Results.Redirect(prefix);
        });

        group.MapPost("", async (
            HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer, IServiceRequestFileStorage fileStorage,
            IOptions<JourneyOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var userId = options.ResolveUserId(ctx);
            var profile = options.ResolveAccessProfile!(ctx);
            var tenantId = options.ResolveTenantId!(ctx);
            var current = engine.GetCurrent(blueprintKey, tenantId, userId, profile);

            var form = await ctx.Request.ReadFormAsync();
            var action = form["action"].ToString();
            var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
            var fieldValues = GovUkStageJourney.CoerceFieldValues(form, current.Render);

            var fileErrors = await StageFileUploads.ApplyFileUploadsAsync(form, current.Render, current.InstanceId, fileStorage, fieldValues);
            if (fileErrors.Count > 0)
            {
                return Results.Content(
                    options.RenderPage!(pageTitle, renderer.RenderJourneyBody(current with { Problems = fileErrors }, prefix), ctx), "text/html");
            }

            var result = engine.Advance(current.InstanceId, tenantId, userId, profile, action, stateVersion, fieldValues);

            // A rejected submission (missing/invalid field values, or a field key that isn't even
            // declared on this stage — see ProcessManagerEngine.Advance's server-side validation)
            // never changes instance state, so stateVersion is still current — safe to render this
            // response directly rather than redirect, unlike a genuine advance below.
            if (result.Problems.Count > 0 && result.Render is not null)
            {
                return Results.Content(
                    options.RenderPage!(pageTitle, renderer.RenderJourneyBody(result, prefix), ctx), "text/html");
            }

            // Redirect rather than render the result directly (POST-redirect-GET): rendering at the
            // POST URL leaves that response in browser history, so reloading it — or a caseworker
            // advancing the same instance from another tab — resubmits the same stale stateVersion
            // and fails with a spurious VERSION_MISMATCH. Redirecting to the GET route always
            // re-fetches whatever the instance's current state actually is.
            return Results.Redirect(prefix);
        });

        return group;
    }
}
