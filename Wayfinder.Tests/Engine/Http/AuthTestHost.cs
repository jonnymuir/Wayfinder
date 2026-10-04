using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// A tiny in-memory ASP.NET host for the authorization regression suites: a header-driven test
/// authentication scheme (authenticated iff <c>X-Test-User</c> is present; carries <c>role=admin</c>
/// iff its value is <c>admin</c>) and the two named policies the surfaces under test use.
/// </summary>
internal static class AuthTestHost
{
    public const string BlueprintsAdminPolicy = "BlueprintsAdmin";
    public const string CaseworkerPolicy = "Caseworker";

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

    public static (HttpClient Client, IReadOnlyList<RouteEndpoint> Endpoints) Build(
        Action<IServiceCollection> addServices, Action<IEndpointRouteBuilder> map)
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
                    s.AddAuthorization(o =>
                    {
                        o.AddPolicy(BlueprintsAdminPolicy, p => p.RequireClaim("role", "admin"));
                        o.AddPolicy(CaseworkerPolicy, p => p.RequireClaim("role", "admin"));
                    });
                    addServices(s);
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
}
