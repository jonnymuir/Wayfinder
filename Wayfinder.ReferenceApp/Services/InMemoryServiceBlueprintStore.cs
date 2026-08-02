using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// Seeds the runtime engine from compiled-in <see cref="ServiceBlueprint"/> definitions —
/// no filesystem read, so the reference host stays genuinely in-memory. Compare
/// <see cref="Wayfinder.Engine.Stores.SingleDefinitionServiceBlueprintStore"/>, this repo's
/// existing single-definition equivalent; this is that same idea for more than one seed.
/// </summary>
public sealed class InMemoryServiceBlueprintStore(params ServiceBlueprint[] definitions) : IServiceBlueprintStore
{
    public IReadOnlyDictionary<string, ServiceBlueprint> LoadDefinitions(ILogger logger)
    {
        var byKey = definitions.ToDictionary(d => d.DefinitionKey, StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("Reference app seeded {Count} in-memory blueprint definition(s).", byKey.Count);
        return byKey;
    }
}
