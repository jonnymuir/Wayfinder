using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Api;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Tests.Engine.Services;

namespace Wayfinder.Tests.Engine.Http;

public class DesignNoteApiTests
{
    private const string Notes = "/wayfinder/service-blueprint-authoring/blueprints/licence/notes";

    private static HttpClient Client()
    {
        var blueprints = new DesignNoteServiceTests.BlueprintStore();
        blueprints.Put(DesignNoteServiceTests.Licence());
        var (client, _) = AuthTestHost.Build(
            s =>
            {
                s.AddSingleton<IServiceBlueprintSourceStore>(blueprints);
                s.AddServiceBlueprintAuthoring();
            },
            e => e.MapServiceBlueprintAuthoringApi(authorizationPolicy: AuthTestHost.BlueprintsAdminPolicy));
        return client;
    }

    private static HttpRequestMessage As(string user, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Test-User", user);
        return request;
    }

    [Fact]
    public async Task ANoteMadeInTheEditor_IsAttributedToTheSignedInPerson_AndListed()
    {
        var client = Client();

        var created = await client.SendAsync(As("admin", HttpMethod.Post, Notes,
            new { anchor = new { kind = "Stage", key = "check" }, text = "Too many questions here" }));
        var listed = await client.SendAsync(As("admin", HttpMethod.Get, Notes));

        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var notes = await listed.Content.ReadFromJsonAsync<DesignNote[]>();
        notes.Should().NotBeNull();
        notes!.Single().Author.Should().Be(new DesignNoteAuthor("admin", DesignNoteAuthorKind.Person));
        notes!.Single().Anchor.Should().Be(new DesignNoteAnchor(DesignNoteAnchorKind.Stage, "check"));
    }

    [Fact]
    public async Task ANoteOnSomethingThatIsNotThere_IsABadRequest_WithTheReason()
    {
        var response = await Client().SendAsync(As("admin", HttpMethod.Post, Notes,
            new { anchor = new { kind = "Stage", key = "nope" }, text = "Hello" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Contain("no stage 'nope'");
    }

    [Fact]
    public async Task SomeoneWhoCannotEditBlueprints_CannotReadOrWriteTheirNotes()
    {
        var client = Client();

        var read = await client.SendAsync(As("casual-user", HttpMethod.Get, Notes));
        var write = await client.SendAsync(As("casual-user", HttpMethod.Post, Notes,
            new { anchor = new { kind = "Blueprint" }, text = "Hello" }));

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
