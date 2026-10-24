using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Api;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Tests.Engine.Services;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>The REST authoring API carries a whole blueprint as YAML, in both directions.</summary>
public class AuthoringApiYamlTests
{
    private const string Blueprints = "/wayfinder/service-blueprint-authoring/blueprints";

    private readonly DesignNoteServiceTests.BlueprintStore _store = new();
    private readonly HttpClient _client;

    public AuthoringApiYamlTests()
    {
        _store.Put(DesignNoteServiceTests.Licence(version: 3));
        _client = AuthTestHost.Build(
            s =>
            {
                s.AddSingleton<IServiceBlueprintSourceStore>(_store);
                s.AddServiceBlueprintAuthoring();
            },
            e => e.MapServiceBlueprintAuthoringApi(authorizationPolicy: AuthTestHost.BlueprintsAdminPolicy)).Client;
    }

    private static HttpRequestMessage AsAdmin(HttpMethod method, string url, string? yaml = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = yaml is null ? null : new StringContent(yaml, Encoding.UTF8, "application/yaml"),
        };
        request.Headers.Add("X-Test-User", "admin");
        return request;
    }

    [Fact]
    public async Task ReadingABlueprint_ReturnsItsYaml()
    {
        var response = await _client.SendAsync(AsAdmin(HttpMethod.Get, $"{Blueprints}/licence"));

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/yaml");
        var yaml = await response.Content.ReadAsStringAsync();
        yaml.Should().StartWith("definitionKey: licence\n");
        ServiceBlueprintYaml.Deserialize(yaml).Version.Should().Be(3);
    }

    [Fact]
    public async Task SavingEditedYaml_PersistsTheChange()
    {
        var read = await (await _client.SendAsync(AsAdmin(HttpMethod.Get, $"{Blueprints}/licence"))).Content.ReadAsStringAsync();
        var edited = read.Replace("displayName: Apply for a licence", "displayName: Apply for a juggling licence", StringComparison.Ordinal);

        var response = await _client.SendAsync(AsAdmin(HttpMethod.Put, $"{Blueprints}/licence", edited));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _store.LoadAsync("licence"))!.DisplayName.Should().Be("Apply for a juggling licence");
    }

    [Fact]
    public async Task SavingYamlThatIsNotABlueprint_IsABadRequest_WithTheLineAtFault()
    {
        var response = await _client.SendAsync(AsAdmin(HttpMethod.Put, $"{Blueprints}/licence", "definitionKey: licence\nstagez: []\n"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var outcome = await response.Content.ReadFromJsonAsync<ServiceBlueprintSaveOutcome>();
        outcome!.Diagnostics.Should().ContainSingle(d => d.Code == "INVALID_YAML" && d.Message.Contains("Line 2", StringComparison.Ordinal));
        (await _store.LoadAsync("licence"))!.Version.Should().Be(3);
    }

    [Fact]
    public async Task ValidatingYaml_ReportsTheSameDiagnosticsASaveWould()
    {
        const string routeStraightToAStage = """
            definitionKey: x
            initialStage: a
            stages:
              - stageKey: a
                routes:
                  - id: a--continue--b
                    target: b
                    trigger: continue
              - stageKey: b
            """;

        var response = await _client.SendAsync(AsAdmin(HttpMethod.Post, $"{Blueprints}/validate", routeStraightToAStage));

        var outcome = await response.Content.ReadFromJsonAsync<ServiceBlueprintValidationOutcome>();
        outcome!.IsValid.Should().BeFalse();
        outcome.Diagnostics.Should().NotContain(d => d.Code == "INVALID_YAML");
    }
}
