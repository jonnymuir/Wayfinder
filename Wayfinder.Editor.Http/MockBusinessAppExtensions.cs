using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Editor.Http;

/// <summary>
/// Implements the contract Wayfinder.Editor's packaged service-blueprint-editor.html demo page
/// expects, via its bundled <c>MockBusinessAppServiceBlueprintSource</c> TS example — see this
/// package's own README. Ported verbatim from Wayfinder.ReferenceApp/Program.cs, where it was
/// hand-copied against the same live <see cref="ServiceBlueprintAuthoringService"/> every other
/// authoring surface (REST/MCP) already uses, so the packaged editor works out of the box without
/// forking Wayfinder.Editor.Client's build.
/// </summary>
public static class MockBusinessAppExtensions
{
    public static RouteGroupBuilder MapMockBusinessAppServiceBlueprints(
        this IEndpointRouteBuilder endpoints, string prefix = "/mockapp/service-blueprints")
    {
        var group = endpoints.MapGroup(prefix);

        group.MapGet("", async (ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
            Results.Json(await authoring.ListAsync(ct)));

        group.MapGet("/{key}", async (string key, ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
        {
            var blueprint = await authoring.ReadAsync(key, ct);
            return blueprint is null ? Results.NotFound() : Results.Json(blueprint);
        });

        group.MapPut("/{key}", async (string key, HttpContext ctx, ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
        {
            var blueprint = await ctx.Request.ReadFromJsonAsync<ServiceBlueprint>(ct);
            if (blueprint is null || !string.Equals(blueprint.DefinitionKey, key, StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }

            var outcome = await authoring.SaveAsync(blueprint, blueprint.Version, ct);
            return outcome.Status switch
            {
                ServiceBlueprintSaveStatus.Saved => Results.NoContent(),
                ServiceBlueprintSaveStatus.Conflict => Results.Conflict(outcome),
                _ => Results.BadRequest(outcome)
            };
        });

        return group;
    }
}
