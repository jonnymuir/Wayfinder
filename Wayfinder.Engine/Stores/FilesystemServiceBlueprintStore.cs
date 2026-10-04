using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Stores;

public sealed partial class FilesystemServiceBlueprintStore(string blueprintSeedPath) : IServiceBlueprintStore
{
    public IReadOnlyDictionary<string, ServiceBlueprint> LoadDefinitions(ILogger logger)
    {
        var definitions = new Dictionary<string, ServiceBlueprint>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(blueprintSeedPath))
        {
            SeedDirectoryMissing(logger, blueprintSeedPath);
            return definitions;
        }

        foreach (var file in Directory.GetFiles(blueprintSeedPath, "*.json"))
        {
            try
            {
                var definition = JsonSerializer.Deserialize<ServiceBlueprint>(
                    File.ReadAllText(file),
                    ServiceBlueprintJson.ReadOptions);

                if (definition == null || string.IsNullOrWhiteSpace(definition.DefinitionKey))
                {
                    continue;
                }

                definitions[definition.DefinitionKey] = definition;
                BlueprintLoaded(logger, definition.DefinitionKey, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                BlueprintLoadFailed(logger, ex, file);
            }
        }

        return definitions;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "workflow-seeds directory not found at {Path}; no service blueprints loaded.")]
    private static partial void SeedDirectoryMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded service blueprint '{Key}' from {File}")]
    private static partial void BlueprintLoaded(ILogger logger, string key, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load service blueprint from {File}")]
    private static partial void BlueprintLoadFailed(ILogger logger, Exception exception, string file);
}
