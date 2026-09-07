using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wayfinder.Engine.Http;

/// <summary>
/// Opt-in CSRF protection for the Wayfinder route surfaces (<c>MapWorklist</c>,
/// <c>MapBulkDatasetReview</c>, <c>MapJourney</c>). These packages ship the routes with no auth or
/// antiforgery opinion of their own — a host adds <c>.RequireAuthorization(...)</c> and, if its
/// mutating endpoints are reached from a browser with an ambient cookie, calls
/// <see cref="ValidateWayfinderAntiforgery{TBuilder}"/> on the same group:
/// <code>
/// app.MapWorklist(prefix: "/caseworker/queue")
///    .RequireAuthorization("Caseworker")
///    .ValidateWayfinderAntiforgery();
/// </code>
/// The GET handlers in those packages then mint a request token via
/// <see cref="MintRequestVerificationToken"/> and render it into their forms / the bulk-data
/// component, so the browser posts it back. A host that authenticates these routes with a bearer
/// token instead (no ambient cookie) does not need this — CSRF does not apply there.
///
/// Requires <c>builder.Services.AddAntiforgery()</c> and <c>app.UseAntiforgery()</c>.
/// </summary>
public static class WayfinderAntiforgery
{
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    /// <summary>
    /// Validates the ASP.NET antiforgery token on every non-safe (POST/PUT/PATCH/DELETE) request to
    /// the group, returning <c>400</c> with a small JSON body when it is missing or invalid.
    /// </summary>
    public static TBuilder ValidateWayfinderAntiforgery<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if (Array.IndexOf(SafeMethods, request.Method) < 0)
            {
                var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(context.HttpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.BadRequest(new { error = "Missing or invalid antiforgery token." });
                }
            }

            return await next(context);
        });

        return builder;
    }

    /// <summary>
    /// Mints the antiforgery request token for this response and stores its paired cookie — call
    /// it at the top of a GET handler (before the response starts), then hand the returned value
    /// to the renderer. Returns <see langword="null"/> when antiforgery services are not
    /// registered, so a host that never calls <see cref="ValidateWayfinderAntiforgery{TBuilder}"/>
    /// pays nothing and its forms carry no token field.
    /// </summary>
    public static string? MintRequestVerificationToken(HttpContext ctx)
    {
        var antiforgery = ctx.RequestServices.GetService<IAntiforgery>();
        return antiforgery?.GetAndStoreTokens(ctx).RequestToken;
    }
}
