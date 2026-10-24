using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// The README quickstart is the first code a newcomer runs, so it is executed as written: the
/// blueprint is read out of README.md itself, and the host wiring is exactly the README's two
/// registrations. Editing the README into something that does not run turns this red.
/// </summary>
public sealed class ReadmeQuickstartTests : IDisposable
{
    private readonly string _blueprintFolder = Directory.CreateTempSubdirectory("wayfinder-quickstart-").FullName;

    public void Dispose() => Directory.Delete(_blueprintFolder, recursive: true);

    [Fact]
    public void TheReadmeBlueprintPassesTheSameValidationAnAuthoringSaveApplies()
    {
        var blueprint = ServiceBlueprintYaml.Deserialize(ReadmeBlueprintYaml());

        var outcome = new ServiceBlueprintAuthoringService(new UnusedSourceStore()).Validate(blueprint);

        outcome.Diagnostics.Where(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void AHostWiredOnlyAsTheReadmeShows_RunsTheJourneyToCompletion()
    {
        File.WriteAllText(Path.Combine(_blueprintFolder, "apply-for-a-licence.yaml"), ReadmeBlueprintYaml());
        var services = new ServiceCollection();
        services.AddProcessManager(_blueprintFolder);
        services.AddSingleton<IServiceContentSanitizer, PassthroughContentSanitizer>();
        using var provider = services.BuildServiceProvider();
        var processManager = provider.GetRequiredService<IProcessManager>();

        var envelope = processManager.GetCurrent(
            blueprintKey: "apply-for-a-licence", tenantId: "default", userId: "user-1",
            accessProfile: ActorProfile.UnrestrictedOwner);
        var advanced = processManager.Advance(
            instanceId: envelope.InstanceId, tenantId: "default", userId: "user-1",
            accessProfile: ActorProfile.UnrestrictedOwner, action: "submit", expectedStateVersion: envelope.StateVersion,
            fieldValues: new Dictionary<string, object?> { ["fullName"] = "Ada Lovelace" });

        envelope.ResponseState.Should().Be("render");
        advanced.ResponseState.Should().Be("complete");
        advanced.Render!.StateDisplayName.Should().Be("Application submitted");
    }

    private static string ReadmeBlueprintYaml()
    {
        var readme = File.ReadAllText(FindReadme());
        var block = Regex.Match(readme, @"```yaml\s*\n# blueprints/apply-for-a-licence\.yaml\n(?<yaml>.*?)```", RegexOptions.Singleline);
        block.Success.Should().BeTrue("README.md's quickstart must keep its blueprints/apply-for-a-licence.yaml block");
        return block.Groups["yaml"].Value;
    }

    private static string FindReadme()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "README.md");
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "Wayfinder.slnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("README.md not found walking up from the test bin.");
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
