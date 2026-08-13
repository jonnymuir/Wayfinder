using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Calculations;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Api;

/// <summary>
/// Maps the Wayfinder service blueprint authoring toolkit's HTTP surface — list/read/validate/save/simulate —
/// onto <see cref="ServiceBlueprintAuthoringService"/>. The returned <see cref="RouteGroupBuilder"/> lets
/// the host chain its own policy, e.g. <c>.RequireAuthorization()</c>; this extension applies none.
/// The host must have already registered <c>ServiceBlueprintAuthoringService</c> (see
/// <c>AddServiceBlueprintAuthoring()</c>), its own <c>IServiceBlueprintSourceStore</c>, and
/// <see cref="AddServiceBlueprintAuthoringApi"/> (see its own remarks — required, not optional,
/// for a registry-registered component type to actually work through this surface).
/// </summary>
public static class ServiceBlueprintAuthoringApiExtensions
{
    /// <summary>
    /// Wires <see cref="ComponentTypeRegistry"/>'s runtime-polymorphic resolver into ASP.NET
    /// Core's own request/response JSON options — minimal API's implicit <c>[FromBody]</c>
    /// binding (used by every <c>ServiceBlueprint blueprint</c> parameter below) reads
    /// <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>, a completely different, separately-
    /// configured <see cref="System.Text.Json.JsonSerializerOptions"/> instance to
    /// <see cref="ServiceBlueprintJson"/>'s — every other read/write path in this toolkit already
    /// goes through <c>ServiceBlueprintJson.ReadOptions</c>/<c>WriteOptions</c> and so already
    /// picks up custom-registered types automatically, but this one didn't, silently: a built-in
    /// component type still round-tripped (seeded onto <c>Component</c> itself via
    /// <c>[JsonDerivedType]</c>, so it works even through options this method never touched), but
    /// any type registered only via <see cref="ComponentTypeRegistry.Register{TComponent}"/> —
    /// exactly the extensibility case this whole registry exists for — failed to deserialize with
    /// an "unrecognized type discriminator" 400 the moment it reached this REST surface, even
    /// though the very same JSON round-tripped correctly everywhere else (MCP tools, direct
    /// <c>ServiceBlueprintAuthoringService</c> calls, the engine's own definition loading). Call
    /// this once at startup, alongside <c>AddServiceBlueprintAuthoring()</c>, before
    /// <c>MapServiceBlueprintAuthoringApi()</c>.
    /// </summary>
    public static IServiceCollection AddServiceBlueprintAuthoringApi(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolver = ComponentTypeRegistry.CreateJsonTypeInfoResolver();
        });

    public static RouteGroupBuilder MapServiceBlueprintAuthoringApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/wayfinder/service-blueprint-authoring")
    {
        var group = endpoints.MapGroup(prefix);

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

        group.MapGet("/blueprints/{definitionKey}", async (
            string definitionKey,
            ServiceBlueprintAuthoringService service,
            CancellationToken ct) =>
        {
            var blueprint = await service.ReadAsync(definitionKey, ct);
            return blueprint is null ? Results.NotFound() : Results.Ok(blueprint);
        });

        group.MapPost("/blueprints/validate", (ServiceBlueprint blueprint, ServiceBlueprintAuthoringService service) =>
            Results.Ok(service.Validate(blueprint)));

        // The body's own `version` (already round-tripped by any client that loaded the blueprint
        // first) IS the expected version for the optimistic-concurrency check — no separate field
        // needed. The store ignores it as a *value to write*; it only compares against it, then
        // authoritatively sets the new persisted version itself.
        group.MapPut("/blueprints/{definitionKey}", async (
            string definitionKey,
            ServiceBlueprint blueprint,
            ServiceBlueprintAuthoringService service,
            CancellationToken ct) =>
        {
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

        group.MapPost("/blueprints/simulate", (SimulateServiceBlueprintRequest request, ServiceBlueprintAuthoringService service) =>
        {
            var mockServiceInputs = request.MockServiceInputs is { } element
                ? (IReadOnlyDictionary<string, object?>)CalculationScopeJson.ToScopeValue(element)!
                : null;
            return Results.Ok(service.Simulate(request.Blueprint, request.Steps, mockServiceInputs));
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

        return group;
    }
}
