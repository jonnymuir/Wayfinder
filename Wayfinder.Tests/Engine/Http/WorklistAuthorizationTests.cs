using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Engine.Worklist;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// Security regression checks for the caseworker worklist and bulk-dataset review routes. Pickup assigns
/// work to a named person and the bulk routes rewrite an uploaded dataset, so they are deny-by-default
/// (CLAUDE.md security rule 3): a host that maps them and forgets to chain a policy must not serve them
/// anonymously. The citizen-facing MapJourney is a different surface and is deliberately not covered here.
/// </summary>
public class WorklistAuthorizationTests
{
    private const string DefinitionKey = "worklist-auth-test";

    private static void AddWorklistServices(IServiceCollection s)
    {
        var definition = new ServiceBlueprint
        {
            DefinitionKey = DefinitionKey,
            DisplayName = "Worklist auth test",
            InitialStage = "only",
            Stages = [new StageDefinition { StageKey = "only", DisplayName = "Only" }]
        };
        s.AddSingleton<IProcessManager>(_ => new ProcessManagerEngine(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer()));
        s.AddSingleton<IServiceRequestFileStorage, InMemoryServiceRequestFileStorage>();
        s.AddSingleton<GovUkComponentRenderer>();
        s.AddSingleton<IBulkDatasetStore>(sp => new InMemoryBulkDatasetStore(sp.GetRequiredService<IServiceRequestFileStorage>()));
        s.AddWorklist(o =>
        {
            o.ResolveTenantId = _ => "tenant";
            o.ResolveAccessProfile = _ => new ActorProfile { VisibleQueues = [], StartableQueues = [], ActionableQueues = [] };
            o.RenderPage = (_, body, _) => body;
        });
    }

    private static void MapBoth(IEndpointRouteBuilder e, string? policy = null, bool allowAnonymous = false)
    {
        e.MapWorklist("/caseworker/queue", policy, allowAnonymous);
        e.MapBulkDatasetReview("/caseworker/queue", policy, allowAnonymous);
    }

    [Fact]
    public async Task ByDefault_EveryWorklistAndBulkReviewRoute_RequiresAuthorization_AndAnUnauthenticatedCallerIsChallenged()
    {
        var (client, endpoints) = AuthTestHost.Build(AddWorklistServices, e => MapBoth(e));

        endpoints.Should().HaveCountGreaterThan(10, "the worklist and bulk-review groups map a dozen-plus routes");
        endpoints.Should().OnlyContain(
            endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() != null && endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null,
            "no worklist or bulk-review route may be left open unless the host opts out explicitly");

        foreach (var endpoint in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var url = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText ?? "/", @"\{[^}]+\}", "x");

            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            response.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
                $"{method} {url} must not serve an unauthenticated caller");
        }
    }

    [Fact]
    public async Task ANamedPolicy_RejectsAnAuthenticatedCallerWhoLacksIt_WithForbidden()
    {
        var (client, endpoints) = AuthTestHost.Build(AddWorklistServices, e => MapBoth(e, policy: AuthTestHost.CaseworkerPolicy));
        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AuthTestHost.CaseworkerPolicy));

        var request = new HttpRequestMessage(HttpMethod.Get, "/caseworker/queue");
        request.Headers.Add("X-Test-User", "not-a-caseworker");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void OptingOutRequiresAnExplicitAllowAnonymous()
    {
        var (_, endpoints) = AuthTestHost.Build(AddWorklistServices, e => MapBoth(e, allowAnonymous: true));

        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null);
    }

    [Fact]
    public void AnAuthorizationPolicyAndAllowAnonymousTogether_AreRejected()
    {
        var act = () => AuthTestHost.Build(AddWorklistServices, e => e.MapWorklist("/q", AuthTestHost.CaseworkerPolicy, allowAnonymous: true));

        act.Should().Throw<ArgumentException>().WithMessage("*contradict*");
    }
}
