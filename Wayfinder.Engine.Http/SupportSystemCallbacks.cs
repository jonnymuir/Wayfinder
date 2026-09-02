using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Http;

/// <summary>
/// The inbound half of the configuration-driven webhook support system: the callback a consumer
/// (an Umbraco Automate automation, Zapier, Make, n8n, a bespoke service) posts once it has
/// decided an outcome, resolving the caseworker's waiting cursor via
/// <see cref="ProcessManagerEngine.ResolveSupportSystemOutcome"/>. This toolkit's authoring API
/// deliberately ships no runtime routes, so a host maps this itself — the same way it maps
/// <c>GetCurrent</c>/<c>Advance</c>. See docs/guides/support-systems.md.
/// </summary>
public static class SupportSystemCallbacks
{
    /// <summary>The DTO a consumer's callback posts. Only <see cref="OutcomeKey"/> is required.</summary>
    public sealed record CallbackPayload(string OutcomeKey, JsonObject? ResultPayload);

    /// <summary>
    /// Maps <c>POST {basePath}/{invocationId}</c>.
    /// </summary>
    /// <param name="sharedSecret">
    /// The secret the caller must present in the <c>X-Webhook-Secret</c> header (compared in
    /// fixed time). <b>Required in practice</b> — when null, the endpoint logs a warning and
    /// accepts any caller, which is acceptable only when the route is unreachable from outside a
    /// trusted network. The <c>invocationId</c> is an unguessable 128-bit token, but it can appear
    /// in logs and run history, so it is defence-in-depth, not the gate.
    /// </param>
    public static RouteHandlerBuilder MapWebhookSupportSystemCallbacks(
        this IEndpointRouteBuilder endpoints,
        ProcessManagerEngine engine,
        string basePath = "/wayfinder/support-systems/callbacks",
        string? sharedSecret = null) =>
        endpoints.MapWebhookSupportSystemCallbacks(() => engine, basePath, sharedSecret);

    /// <summary>
    /// <inheritdoc cref="MapWebhookSupportSystemCallbacks(IEndpointRouteBuilder, ProcessManagerEngine, string, string?)"/>
    /// <para/>
    /// Takes a <paramref name="resolveEngine"/> accessor rather than an instance so the engine is
    /// resolved lazily on the first callback, not at <c>Map…</c> time. A host whose engine's own
    /// construction reads a database (e.g. an Umbraco host loading blueprint definitions) must use
    /// this overload — resolving the engine eagerly during <c>Program.cs</c> would run before the
    /// host's schema migrations.
    /// </summary>
    public static RouteHandlerBuilder MapWebhookSupportSystemCallbacks(
        this IEndpointRouteBuilder endpoints,
        Func<ProcessManagerEngine> resolveEngine,
        string basePath = "/wayfinder/support-systems/callbacks",
        string? sharedSecret = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveEngine);

        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wayfinder.Engine.Http.SupportSystemCallbacks");

        if (string.IsNullOrEmpty(sharedSecret))
        {
            logger.LogWarning(
                "Support-system callback route {Path}/{{invocationId}} is mapped with NO shared secret — " +
                "any caller that reaches it can resolve a pending invocation. Acceptable only on a trusted " +
                "network.", basePath);
        }

        return endpoints.MapPost($"{basePath}/{{invocationId}}", (
            string invocationId, CallbackPayload payload, HttpRequest request) =>
        {
            if (!string.IsNullOrEmpty(sharedSecret))
            {
                var presented = request.Headers["X-Webhook-Secret"].ToString();
                if (string.IsNullOrEmpty(presented) || !FixedTimeEquals(presented, sharedSecret))
                {
                    logger.LogWarning(
                        "Rejected support-system callback for invocation {InvocationId}: missing/invalid secret.",
                        invocationId);
                    return Results.Unauthorized();
                }
            }

            if (string.IsNullOrWhiteSpace(payload.OutcomeKey))
            {
                return Results.BadRequest(new { error = "outcomeKey is required." });
            }

            var result = resolveEngine().ResolveSupportSystemOutcome(invocationId, payload.OutcomeKey, payload.ResultPayload);

            if (result.ResponseState != "error")
            {
                return Results.Ok(new { status = "resolved", outcome = payload.OutcomeKey });
            }

            var code = result.Problems.Count > 0 ? result.Problems[0].Code : "";
            return code switch
            {
                // Unknown id or an already-resolved invocation (the engine collapses both). Treat
                // as an idempotent no-op so a retrying caller does not storm the route; logged so
                // a genuinely wrong id is still diagnosable.
                "SUPPORT_SYSTEM_INVOCATION_NOT_FOUND" => LogAndOk(invocationId),
                // The caller sent an outcome the capability never declared — a real client bug.
                "SUPPORT_SYSTEM_INVALID_OUTCOME" => Results.BadRequest(new { error = result.Problems[0].Message }),
                _ => Results.Problem(
                    result.Problems.Count > 0 ? result.Problems[0].Message : "Failed to resolve outcome."),
            };

            IResult LogAndOk(string id)
            {
                logger.LogInformation(
                    "Support-system callback for invocation {InvocationId} was a no-op (unknown or already resolved).",
                    id);
                return Results.Ok(new { status = "no-op" });
            }
        });
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
