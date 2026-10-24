using System.Text.RegularExpressions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Stores;

/// <summary>
/// File-backed, dependency-light implementation of <see cref="IServiceBlueprintSourceStore"/> — one
/// <c>{definitionKey}.yaml</c> file per blueprint under <paramref name="basePath"/>, read and
/// written with <see cref="ServiceBlueprintYaml"/> (no editor/projector dependency), so it's usable
/// by any standalone host — e.g. the MCP server, which runs as its own process and can't share
/// a running app's in-memory engine.
/// </summary>
public sealed class FilesystemServiceBlueprintSourceStore(string basePath) : IServiceBlueprintSourceStore, IDisposable
{
    // Serializes save's read-check-write so the version compare-and-swap is atomic within this
    // process. Sufficient for a single-process reference app; a real multi-process file-backed
    // store needs OS file locking, and a real database-backed store should use an atomic
    // UPDATE ... WHERE Version = @expectedVersion instead of a lock at all.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    // A definition key is turned straight into a filename, so it must be a plain slug —
    // letters, digits, '-' and '_', starting and ending alphanumeric. This allowlist is
    // what makes an externally-supplied key safe to put in a path at all; the
    // base-directory containment check below is kept as a defence-in-depth second barrier.
    private static readonly Regex DefinitionKeyPattern =
        new("^[A-Za-z0-9](?:[A-Za-z0-9_-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    private string ResolveSafePath(string definitionKey)
    {
        if (definitionKey is null || !DefinitionKeyPattern.IsMatch(definitionKey))
        {
            throw new InvalidOperationException(
                $"Invalid blueprint definition key '{definitionKey}'. A key must be a slug of letters, digits, '-' and '_'.");
        }

        var combined = Path.Combine(basePath, definitionKey + ".yaml");
        var resolved = Path.GetFullPath(combined);
        var baseFull = Path.GetFullPath(basePath);
        if (!resolved.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolved, baseFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resolved path '{resolved}' escapes blueprint source base directory '{baseFull}'.");
        }
        return resolved;
    }

    public async Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(basePath))
            return Array.Empty<ServiceBlueprintSourceSummary>();

        var summaries = new List<ServiceBlueprintSourceSummary>();
        foreach (var path in Directory.EnumerateFiles(basePath, "*.yaml"))
        {
            var blueprint = ServiceBlueprintYaml.Deserialize(await File.ReadAllTextAsync(path, ct));
            summaries.Add(new ServiceBlueprintSourceSummary(blueprint.DefinitionKey, blueprint.DisplayName));
        }

        return summaries
            .OrderBy(summary => summary.DefinitionKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default)
    {
        var path = ResolveSafePath(definitionKey);
        if (!File.Exists(path))
            return null;

        return ServiceBlueprintYaml.Deserialize(await File.ReadAllTextAsync(path, ct));
    }

    public async Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(blueprint.DefinitionKey, ct);
            var currentVersion = current?.Version ?? 0;
            if (currentVersion != expectedVersion)
            {
                var existingPath = ResolveSafePath(blueprint.DefinitionKey);
                return new ServiceBlueprintSaveResult(Saved: false, CurrentVersion: currentVersion, Location: existingPath);
            }

            Directory.CreateDirectory(basePath);
            var newVersion = expectedVersion + 1;
            var toSave = blueprint with { Version = newVersion };

            var path = ResolveSafePath(blueprint.DefinitionKey);
            await File.WriteAllTextAsync(path, ServiceBlueprintYaml.Serialize(toSave), ct);
            return new ServiceBlueprintSaveResult(Saved: true, CurrentVersion: newVersion, Location: path);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default)
    {
        var path = ResolveSafePath(definitionKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public void Dispose() => _saveLock.Dispose();
}
