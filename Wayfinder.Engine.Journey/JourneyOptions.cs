using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Journey;

/// <summary>
/// Configuration for <see cref="JourneyExtensions.MapJourney"/> — see this package's own README.
/// <see cref="ResolveTenantId"/>/<see cref="ResolveAccessProfile"/>/<see cref="RenderPage"/> have no
/// sane universal default and are validated as required at startup (see
/// <see cref="JourneyExtensions.AddJourney"/>); everything else has a genuinely generic default a
/// host overrides only if it wants something different. Shared across every <c>MapJourney</c> call
/// a host makes — a journey's own identity (blueprint, URL, page title) is supplied per call
/// instead, since a host normally resolves tenant/actor/page-chrome the same way for all of them.
/// </summary>
public sealed class JourneyOptions
{
    /// <summary>Required. How this host resolves the tenant id for the current request.</summary>
    public Func<HttpContext, string>? ResolveTenantId { get; set; }

    /// <summary>Required. How this host resolves the accessing actor's <see cref="ActorProfile"/> for the current request.</summary>
    public Func<HttpContext, ActorProfile>? ResolveAccessProfile { get; set; }

    /// <summary>
    /// Required. The page-chrome escape hatch — this package owns zero HTML skeleton. A host
    /// supplies its own page wrapper, e.g. <c>(title, body, ctx) => PageShell.Render(title, body, ctx.User)</c>.
    /// </summary>
    public Func<string, string, HttpContext, string>? RenderPage { get; set; }

    /// <summary>Defaults to reading <see cref="ClaimTypes.NameIdentifier"/> — override only if this
    /// host resolves the acting user id differently.</summary>
    public Func<HttpContext, string> ResolveUserId { get; set; } = DefaultResolveUserId;

    private static string DefaultResolveUserId(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("Authenticated request has no NameIdentifier claim.");
}
