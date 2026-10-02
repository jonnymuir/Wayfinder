using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wayfinder.Editor.Http;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Api;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Mcp;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// Security regression checks for the three surfaces that can read and overwrite every blueprint: the
/// REST authoring API, the MCP endpoint and the mock-business-app routes. They are deny-by-default
/// (CLAUDE.md security rule 3): a host that maps one and forgets to chain a policy must not end up
/// serving it anonymously. These go red if a route is ever mapped without an authorization requirement.
/// </summary>
public class AuthoringSurfaceAuthorizationTests
{
    private const string AdminPolicy = "BlueprintsAdmin";

    /// <summary>Authenticated iff X-Test-User is present; carries role=admin iff its value is "admin".</summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
            if (user == "admin")
            {
                claims.Add(new Claim("role", "admin"));
            }

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    /// <summary>Nothing here is ever reached: every request in this suite is stopped by authorization first.</summary>
    private sealed class UnusedStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (HttpClient Client, IReadOnlyList<RouteEndpoint> Endpoints) Host(Action<IEndpointRouteBuilder> map)
    {
        IEndpointRouteBuilder? builder = null;
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddLogging();
                    s.AddRouting();
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                    s.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireClaim("role", "admin")));
                    s.AddSingleton<IServiceBlueprintSourceStore, UnusedStore>();
                    s.AddServiceBlueprintAuthoring();
                    s.AddServiceBlueprintAuthoringApi();
                    s.AddServiceBlueprintAuthoringMcp();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        builder = e;
                        map(e);
                    });
                }))
            .Start();

        var endpoints = builder!.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        return (host.GetTestClient(), endpoints);
    }

    public static TheoryData<string, string, Action<IEndpointRouteBuilder>> DenyByDefaultSurfaces() => new()
    {
        { "REST authoring API", "/wayfinder/service-blueprint-authoring/blueprints", e => e.MapServiceBlueprintAuthoringApi() },
        { "MCP endpoint", "/wayfinder/service-blueprint-authoring/mcp", e => e.MapServiceBlueprintAuthoringMcp() },
        { "mock business app", "/mockapp/service-blueprints", e => e.MapMockBusinessAppServiceBlueprints() },
    };

    [Theory]
    [MemberData(nameof(DenyByDefaultSurfaces))]
    public async Task ByDefault_AnUnauthenticatedCallerIsChallenged_AndNoRouteIsLeftWithoutAnAuthorizationRequirement(
        string surface, string path, Action<IEndpointRouteBuilder> map)
    {
        var (client, endpoints) = Host(map);

        endpoints.Should().NotBeEmpty(because: surface);
        endpoints.Should().OnlyContain(
            endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() != null && endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null,
            $"every {surface} route must carry an authorization requirement unless the host opts out explicitly");

        // Every route, called the way it is meant to be called, with no credentials.
        foreach (var endpoint in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var url = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText ?? path, @"\{[^}]+\}", "x");

            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            response.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
                $"{surface}: {method} {url} must not serve an unauthenticated caller");
        }
    }

    [Fact]
    public async Task ANamedPolicy_RejectsAnAuthenticatedCallerWhoLacksIt_WithForbidden()
    {
        var (client, endpoints) = Host(e => e.MapServiceBlueprintAuthoringApi(authorizationPolicy: AdminPolicy));
        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AdminPolicy));

        var request = new HttpRequestMessage(HttpMethod.Get, "/wayfinder/service-blueprint-authoring/blueprints");
        request.Headers.Add("X-Test-User", "casual-user");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "authenticated is not the same as authorized for BlueprintsAdmin");
    }

    [Theory]
    [MemberData(nameof(DenyByDefaultSurfaces))]
    public void OptingOutRequiresAnExplicitAllowAnonymous_AndThenNoRouteDemandsAuthorization(
        string surface, string path, Action<IEndpointRouteBuilder> _)
    {
        Action<IEndpointRouteBuilder> optOut = surface switch
        {
            "REST authoring API" => e => e.MapServiceBlueprintAuthoringApi(allowAnonymous: true),
            "MCP endpoint" => e => e.MapServiceBlueprintAuthoringMcp(allowAnonymous: true),
            _ => e => e.MapMockBusinessAppServiceBlueprints(allowAnonymous: true),
        };

        var (_, endpoints) = Host(optOut);

        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null, $"{surface} {path}");
    }

    [Fact]
    public void AnAuthorizationPolicyAndAllowAnonymousTogether_AreRejected_RatherThanSilentlyPickingOne()
    {
        var act = () => Host(e => e.MapServiceBlueprintAuthoringApi(authorizationPolicy: AdminPolicy, allowAnonymous: true));

        act.Should().Throw<ArgumentException>().WithMessage("*contradict*");
    }
}
