using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Worklist;

/// <summary>
/// Configuration for <see cref="WorklistExtensions.MapWorklist"/> — see docs/guides/work-allocation.md
/// and docs/guides/queue-worklist-filtering.md for the underlying engine surface this wraps.
/// <see cref="ResolveTenantId"/>/<see cref="ResolveAccessProfile"/>/<see cref="RenderPage"/> have no
/// sane universal default and are validated as required at startup (see <see cref="WorklistExtensions.AddWorklist"/>);
/// everything else has a genuinely generic default a host overrides only if it wants something
/// different.
/// </summary>
public sealed class WorklistOptions
{
    /// <summary>Required. How this host resolves the tenant id for the current request.</summary>
    public Func<HttpContext, string>? ResolveTenantId { get; set; }

    /// <summary>
    /// Required. How this host resolves the accessing actor's <see cref="ActorProfile"/> for the
    /// current request — already takes the whole <see cref="HttpContext"/>, not just a static
    /// value, so a host whose eligibility differs per signed-in user (e.g. team-based
    /// <c>QueueDefinition.RoleGates</c>) can resolve it per-request without any change to this
    /// option's own shape.
    /// </summary>
    public Func<HttpContext, ActorProfile>? ResolveAccessProfile { get; set; }

    /// <summary>
    /// Required. The page-chrome escape hatch — this package owns zero HTML skeleton. A host
    /// supplies its own page wrapper, e.g. <c>(title, body, ctx) => PageShell.Render(title, body, ctx.User)</c>.
    /// </summary>
    public Func<string, string, HttpContext, string>? RenderPage { get; set; }

    /// <summary>Defaults to reading <see cref="ClaimTypes.NameIdentifier"/> — override only if this
    /// host resolves the acting user id differently.</summary>
    public Func<HttpContext, string> ResolveUserId { get; set; } = DefaultResolveUserId;

    public int DefaultPageSize { get; set; } = 20;

    public string WorklistPageTitle { get; set; } = "Work queue";

    public string ReviewPageTitle { get; set; } = "Review item";

    public string TeamWorklistPageTitle { get; set; } = "Team work queue";

    /// <summary>
    /// Optional. If set, <see cref="WorklistExtensions.MapWorklist"/> also exposes a team-view
    /// route (<c>{prefix}/team/{teamId}</c>) — a team's own aggregate view of everything it owns
    /// (see <c>IProcessManager.GetTeamWorkItems</c> and docs/guides/team-assignment.md), alongside
    /// the personal worklist at <c>{prefix}</c>. Returns every team the current actor belongs to
    /// that's worth surfacing a link for (id, display name) — used to render a small "my work /
    /// my team's work" nav at the top of both pages. A host with no team-owned queues can leave
    /// this unset; the personal worklist works unchanged either way.
    /// </summary>
    public Func<HttpContext, IReadOnlyList<(string TeamId, string DisplayName)>>? ResolveTeams { get; set; }

    private static string DefaultResolveUserId(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("Authenticated request has no NameIdentifier claim.");
}
