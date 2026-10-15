using System.Text.Json;
using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// The guides teach from the blueprints in <c>docs/examples/</c>, so every one of them must pass
/// the same validation an authoring save applies. A model change that breaks an example turns
/// this red instead of leaving the guides describing a blueprint that no longer loads.
/// </summary>
public class DocumentedExampleBlueprintTests
{
    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(ExamplesFolder(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryDocumentedExampleIsAValidBlueprint(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(ExamplesFolder(), fileName));
        var blueprint = JsonSerializer.Deserialize<ServiceBlueprint>(json, ServiceBlueprintJson.ReadOptions)!;

        var outcome = new ServiceBlueprintAuthoringService(new UnusedSourceStore()).Validate(blueprint);

        outcome.Diagnostics.Where(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error).Should().BeEmpty();
    }

    private static string ExamplesFolder()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "examples");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "Wayfinder.slnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("docs/examples not found walking up from the test bin.");
    }

    private sealed class UnusedSourceStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
