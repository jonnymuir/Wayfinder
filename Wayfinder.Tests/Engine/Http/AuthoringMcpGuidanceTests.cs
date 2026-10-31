using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Mcp;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// What an agent connecting to the authoring MCP endpoint is told before it writes anything: the
/// server's instructions, the "what belongs in a blueprint" guide, and the ready-made prompts. These
/// are what steer an agent away from rebuilding a service in host code, in any MCP client.
/// </summary>
public class AuthoringMcpGuidanceTests
{
    private sealed class EmptyStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceBlueprintSourceSummary>>([]);
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) => Task.FromResult<ServiceBlueprint?>(null);
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static async Task<McpClient> ConnectAsync(string? hostInstructions = null)
    {
        var (client, _) = AuthTestHost.Build(
            s =>
            {
                s.AddSingleton<IServiceBlueprintSourceStore, EmptyStore>();
                s.AddServiceBlueprintAuthoring();
                s.AddServiceBlueprintAuthoringMcp(hostInstructions);
            },
            e => e.MapServiceBlueprintAuthoringMcp(authorizationPolicy: AuthTestHost.BlueprintsAdminPolicy));
        client.DefaultRequestHeaders.Add("X-Test-User", "admin");

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(client.BaseAddress!, "/wayfinder/service-blueprint-authoring/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            client);

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task AConnectingAgentIsToldToBuildServicesAsBlueprints_AndWhereToReadFirst()
    {
        await using var mcp = await ConnectAsync();

        mcp.ServerInstructions.Should().Contain("Build services as blueprints here")
            .And.Contain("service-blueprint-docs://building-with-wayfinder");
    }

    [Fact]
    public async Task AHostsOwnInstructionsFollowTheDefaults_RatherThanReplacingThem()
    {
        await using var mcp = await ConnectAsync("This host has one public queue.");

        mcp.ServerInstructions.Should().StartWith(ServiceBlueprintAuthoringInstructions.Default)
            .And.EndWith("About this host: This host has one public queue.");
    }

    [Fact]
    public async Task TheBuildingGuideIsReadableAsAResource()
    {
        await using var mcp = await ConnectAsync();

        var result = await mcp.ReadResourceAsync("service-blueprint-docs://building-with-wayfinder");

        result.Contents.OfType<TextResourceContents>().Single().Text
            .Should().Contain("## Where each part of a service lives");
    }

    [Fact]
    public async Task ThePromptsOfferTheThreeWaysADesignPartnerStarts()
    {
        await using var mcp = await ConnectAsync();

        var prompts = await mcp.ListPromptsAsync();

        prompts.Select(p => p.Name).Should().BeEquivalentTo(
            "design-a-service", "convert-an-existing-design", "work-through-design-notes");
    }

    [Fact]
    public async Task ConvertingAnExistingDesign_AsksForTheGapsBeforeBuilding()
    {
        await using var mcp = await ConnectAsync();

        var prompt = await mcp.GetPromptAsync(
            "convert-an-existing-design",
            new Dictionary<string, object?> { ["design"] = "The pensions member portal in ./design" });

        var text = prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("The pensions member portal in ./design")
            .And.Contain("stop after step 5 for my decisions");
    }
}
