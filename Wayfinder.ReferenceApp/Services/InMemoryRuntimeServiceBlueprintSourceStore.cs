using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// The authoring-surface (editor / REST / MCP) counterpart to the JSON seed files in
/// service-blueprints/: layers save-time overrides over whatever's live in the running
/// <see cref="IProcessManager"/>, so a save from any authoring surface calls
/// <see cref="IProcessManager.UpdateDefinition"/> and is immediately visible to the next
/// request — no restart, nothing written to disk. Never persists anywhere else; a process
/// restart forgets every override and returns to the seed files, which is the point — this
/// host is completely transient.
/// </summary>
public sealed class InMemoryRuntimeServiceBlueprintSourceStore(IProcessManager engine) : IServiceBlueprintSourceStore
{
    private readonly Dictionary<string, ServiceBlueprint> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();

    public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default)
    {
        var byDefinitionKey = engine.GetAllDefinitions()
            .ToDictionary(definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase);

        foreach (var (definitionKey, blueprint) in _overrides)
        {
            byDefinitionKey[definitionKey] = blueprint;
        }

        var summaries = byDefinitionKey.Values
            .OrderBy(blueprint => blueprint.DefinitionKey, StringComparer.Ordinal)
            .Select(blueprint => new ServiceBlueprintSourceSummary(blueprint.DefinitionKey, blueprint.DisplayName))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ServiceBlueprintSourceSummary>>(summaries);
    }

    public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default)
    {
        if (_overrides.TryGetValue(definitionKey, out var overridden))
        {
            return Task.FromResult<ServiceBlueprint?>(overridden);
        }

        return Task.FromResult(engine.GetDefinition(definitionKey));
    }

    public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default)
    {
        // Synchronous critical section (dictionary + engine state, no I/O) — a plain lock is
        // enough; a database-backed store would use an atomic UPDATE ... WHERE Version = @expected
        // instead (see Wayfinder.Engine.Api/README.md's optimistic-concurrency notes).
        lock (_saveLock)
        {
            var current = _overrides.TryGetValue(blueprint.DefinitionKey, out var overridden)
                ? overridden
                : engine.GetDefinition(blueprint.DefinitionKey);
            var currentVersion = current?.Version ?? 0;

            if (currentVersion != expectedVersion)
            {
                return Task.FromResult(new ServiceBlueprintSaveResult(
                    Saved: false,
                    CurrentVersion: currentVersion,
                    Location: $"memory://reference-app/blueprints/{blueprint.DefinitionKey}"));
            }

            var newVersion = expectedVersion + 1;
            var toSave = blueprint with { Version = newVersion };

            _overrides[blueprint.DefinitionKey] = toSave;
            engine.UpdateDefinition(blueprint.DefinitionKey, toSave);

            return Task.FromResult(new ServiceBlueprintSaveResult(
                Saved: true,
                CurrentVersion: newVersion,
                Location: $"memory://reference-app/blueprints/{blueprint.DefinitionKey}"));
        }
    }

    public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default)
    {
        lock (_saveLock)
        {
            // Non-short-circuiting | — both removals must run regardless of whether the first
            // found something, since a definition can exist in one without the other (e.g. a
            // seed-only blueprint never overridden by an editor save).
            var existed = _overrides.Remove(definitionKey) | engine.RemoveDefinition(definitionKey);
            return Task.FromResult(existed);
        }
    }

    /// <summary>Clears every override — used by the Development-only <c>/api/test/reset</c> endpoint.</summary>
    public void ClearOverrides()
    {
        lock (_saveLock)
        {
            _overrides.Clear();
        }
    }
}
