using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>
/// The blueprints a running engine can serve, keyed case-insensitively by the lookup key the
/// store supplied (falling back to the blueprint's own <c>DefinitionKey</c>).
/// </summary>
internal sealed partial class BlueprintRegistry
{
    private readonly Dictionary<string, ServiceBlueprint> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public BlueprintRegistry(IServiceBlueprintStore store, ILogger logger)
    {
        _logger = logger;

        foreach (var (lookupKey, definition) in store.LoadDefinitions(logger))
        {
            var runtimeLookupKey = !string.IsNullOrWhiteSpace(lookupKey)
                ? lookupKey
                : definition.DefinitionKey;

            if (!string.IsNullOrWhiteSpace(runtimeLookupKey))
            {
                _definitions[runtimeLookupKey] = definition;
            }
        }

        RuntimeReady(logger, _definitions.Count);
    }

    public IEnumerable<ServiceBlueprint> All => _definitions.Values;

    public bool TryGet(string key, out ServiceBlueprint definition) => _definitions.TryGetValue(key, out definition!);

    public ServiceBlueprint? Find(string key) => _definitions.GetValueOrDefault(key);

    /// <summary>
    /// An upsert, not update-only: a brand-new key (one this engine has never seen, e.g. a blueprint an
    /// agent or human just authored from scratch) must become servable here, or "a save reaches the
    /// live engine immediately" is false for exactly the scenario the authoring toolkit exists for.
    /// </summary>
    public void Upsert(string key, ServiceBlueprint updated)
    {
        var isNewKey = !_definitions.ContainsKey(key);
        _definitions[key] = updated;
        Upserted(_logger, isNewKey ? "registered" : "updated", key);
    }

    public bool Remove(string key) => _definitions.Remove(key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blueprint runtime ready: {Defs} definition(s).")]
    private static partial void RuntimeReady(ILogger logger, int defs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service blueprint {Outcome} in-memory: {Key}")]
    private static partial void Upserted(ILogger logger, string outcome, string key);
}
