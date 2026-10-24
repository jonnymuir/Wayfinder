using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Calculations;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Api;

/// <summary>
/// Maps the Wayfinder service blueprint authoring toolkit's HTTP surface — list/read/validate/save/simulate —
/// onto <see cref="ServiceBlueprintAuthoringService"/>. Deny by default: every route requires an
/// authenticated caller (or the named <c>authorizationPolicy</c>), and the host must write
/// <c>allowAnonymous: true</c> to opt out. The returned <see cref="RouteGroupBuilder"/> still lets
/// the host chain a stricter policy of its own, e.g. <c>.RequireAuthorization("BlueprintsAdmin")</c>.
/// The host must have already registered <c>ServiceBlueprintAuthoringService</c> (see
/// <c>AddServiceBlueprintAuthoring()</c>) and its own <c>IServiceBlueprintSourceStore</c>.
/// A blueprint travels as YAML (<c>application/yaml</c>) in both directions, read and written by
/// <see cref="ServiceBlueprintYaml"/>; catalogs, diagnostics and outcomes are JSON.
/// </summary>
public static class ServiceBlueprintAuthoringApiExtensions
{
    /// <param name="authorizationPolicy">
    /// The policy every route requires. Null means the default policy: any authenticated caller. The host
    /// must have registered authentication and authorization (<c>UseAuthentication</c>/<c>UseAuthorization</c>);
    /// without them the routes fail closed rather than serving anonymously.
    /// </param>
    /// <param name="allowAnonymous">
    /// Explicit opt-out for a demo or a host that protects these routes another way. Write a reason next to
    /// the call: this surface can read and overwrite every blueprint.
    /// </param>
    public static RouteGroupBuilder MapServiceBlueprintAuthoringApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/wayfinder/service-blueprint-authoring",
        string? authorizationPolicy = null,
        bool allowAnonymous = false)
    {
        var group = MapAuthorizedGroup(endpoints, prefix, authorizationPolicy, allowAnonymous);

        group.MapGet("/blueprints", async (ServiceBlueprintAuthoringService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));

        // Read-only, host-wide catalog data — not scoped to any one blueprint, so it lives
        // directly on this toolkit route rather than behind a host's own mockapp-style CRUD
        // mediation (see ServiceBlueprintSource in Wayfinder.Editor.Client). The REST twin of
        // the MCP list_component_types tool, for the browser-based editor: every registered
        // component type (built-in and any host-registered custom one — see
        // docs/guides/extending-the-component-catalog.md), driving the schema-based add/edit UI.
        group.MapGet("/component-types", () => Results.Ok(ComponentTypeRegistry.All));

        // Same reasoning as /component-types above — the REST twin of the MCP
        // list_support_systems tool, driving the stage-action editor's support-system/capability
        // pickers. See docs/guides/support-systems.md.
        group.MapGet("/support-systems", () => Results.Ok(SupportSystemRegistry.All));

        MapBlueprintDocuments(group);
        group.MapDesignNotes();

        return group;
    }

    /// <summary>The routes that carry a whole blueprint, as YAML.</summary>
    private static void MapBlueprintDocuments(RouteGroupBuilder group)
    {
        group.MapGet("/blueprints/{definitionKey}", async (
            string definitionKey,
            ServiceBlueprintAuthoringService service,
            CancellationToken ct) =>
        {
            var blueprint = await service.ReadAsync(definitionKey, ct);
            return blueprint is null ? Results.NotFound() : Results.Text(ServiceBlueprintYaml.Serialize(blueprint), YamlContentType);
        });

        group.MapPost("/blueprints/validate", async (HttpRequest request, ServiceBlueprintAuthoringService service, CancellationToken ct) =>
            ServiceBlueprintYaml.TryDeserialize(await ReadBodyAsync(request, ct), out var blueprint, out var diagnostic)
                ? Results.Ok(service.Validate(blueprint))
                : Results.Ok(new ServiceBlueprintValidationOutcome(false, [diagnostic])));

        // The body's own `version` (already round-tripped by any client that loaded the blueprint
        // first) IS the expected version for the optimistic-concurrency check — no separate field
        // needed. The store ignores it as a *value to write*; it only compares against it, then
        // authoritatively sets the new persisted version itself.
        group.MapPut("/blueprints/{definitionKey}", async (
            string definitionKey,
            HttpRequest request,
            ServiceBlueprintAuthoringService service,
            CancellationToken ct) =>
        {
            if (!ServiceBlueprintYaml.TryDeserialize(await ReadBodyAsync(request, ct), out var blueprint, out var diagnostic))
            {
                return Results.BadRequest(ServiceBlueprintSaveOutcome.Invalid([diagnostic]));
            }

            if (!string.Equals(blueprint.DefinitionKey, definitionKey, StringComparison.Ordinal))
            {
                return Results.BadRequest(new ServiceBlueprintValidationOutcome(
                    false,
                    [new ServiceBlueprintDiagnostic(
                        "ROUTE_KEY_MISMATCH",
                        "definitionKey",
                        $"Route key '{definitionKey}' does not match body definitionKey '{blueprint.DefinitionKey}'.")]));
            }

            var outcome = await service.SaveAsync(blueprint, blueprint.Version, ct);
            return outcome.Status switch
            {
                ServiceBlueprintSaveStatus.Saved => Results.Ok(outcome),
                ServiceBlueprintSaveStatus.Conflict => Results.Conflict(outcome),
                _ => Results.BadRequest(outcome)
            };
        });

        group.MapPost("/blueprints/simulate", (SimulateServiceBlueprintRequest request) =>
        {
            if (!ServiceBlueprintYaml.TryDeserialize(request.Blueprint, out var blueprint, out var diagnostic))
            {
                return Results.BadRequest(new ServiceBlueprintValidationOutcome(false, [diagnostic]));
            }

            var mockServiceInputs = request.MockServiceInputs is { } element
                ? (IReadOnlyDictionary<string, object?>)CalculationScopeJson.ToScopeValue(element)!
                : null;
            return Results.Ok(ServiceBlueprintSimulationRunner.Run(blueprint, request.Steps, mockServiceInputs));
        });

        // Cheap enough to poll: a client that has a blueprint open (e.g. the visual editor) can
        // check every ~15s whether it's still current without re-fetching the full definition.
        // A high-scale host may want a dedicated version-only store lookup instead of loading
        // the whole definition just to read one field; not worth it for the reference store.
        group.MapGet("/blueprints/{definitionKey}/version", async (
            string definitionKey,
            ServiceBlueprintAuthoringService service,
            CancellationToken ct) =>
        {
            var blueprint = await service.ReadAsync(definitionKey, ct);
            return blueprint is null ? Results.NotFound() : Results.Ok(new { version = blueprint.Version });
        });
    }

    private const string YamlContentType = "application/yaml";

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    private static RouteGroupBuilder MapAuthorizedGroup(
        IEndpointRouteBuilder endpoints, string prefix, string? authorizationPolicy, bool allowAnonymous)
    {
        if (allowAnonymous && authorizationPolicy is not null)
        {
            throw new ArgumentException("authorizationPolicy and allowAnonymous: true contradict each other.", nameof(authorizationPolicy));
        }

        var group = endpoints.MapGroup(prefix);
        if (allowAnonymous)
        {
            group.AllowAnonymous();
        }
        else if (authorizationPolicy is null)
        {
            group.RequireAuthorization();
        }
        else
        {
            group.RequireAuthorization(authorizationPolicy);
        }
        return group;
    }
}
