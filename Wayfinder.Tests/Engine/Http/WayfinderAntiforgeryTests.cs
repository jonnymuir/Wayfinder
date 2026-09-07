using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wayfinder.Engine.Http;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// <see cref="WayfinderAntiforgery"/> — the opt-in CSRF guard a cookie-authenticated host chains
/// onto the <c>MapWorklist</c> / <c>MapBulkDatasetReview</c> / <c>MapJourney</c> route groups. The
/// framework-agnostic packages ship those routes with no antiforgery opinion; this filter is how a
/// host adds one without the packages taking a hard ASP.NET-antiforgery dependency at the seam.
/// This is an integrity boundary (a forged cross-site POST advances someone else's application or
/// picks up a caseworker's item), so each behaviour gets its own test.
/// </summary>
public class WayfinderAntiforgeryTests
{
    /// <summary>
    /// A host wired the way <c>Wayfinder.ReferenceApp/Program.cs</c> wires it: antiforgery
    /// services + middleware, a GET that mints a token via
    /// <see cref="WayfinderAntiforgery.MintRequestVerificationToken"/>, and a POST behind
    /// <see cref="WayfinderAntiforgery.ValidateWayfinderAntiforgery{TBuilder}"/>.
    /// </summary>
    private static HttpClient ProtectedHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddLogging();
                    s.AddRouting();
                    s.AddAntiforgery();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAntiforgery();
                    app.UseEndpoints(e =>
                    {
                        var group = e.MapGroup("/g").ValidateWayfinderAntiforgery();
                        group.MapGet("/token", (HttpContext ctx) =>
                            Results.Text(WayfinderAntiforgery.MintRequestVerificationToken(ctx) ?? ""));
                        group.MapPost("/do", () => Results.Ok("done"));
                    });
                }))
            .Start();
        return host.GetTestClient();
    }

    private static async Task<(string Token, string Cookie)> MintTokenAsync(HttpClient http)
    {
        var response = await http.GetAsync("/g/token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadAsStringAsync();
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return (token, cookie);
    }

    [Fact]
    public async Task A_non_safe_request_with_no_token_is_rejected_with_400()
    {
        var http = ProtectedHost();

        var response = await http.PostAsync("/g/do", new StringContent(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a cross-site POST carries no antiforgery token — it must never reach the handler");
    }

    [Fact]
    public async Task A_non_safe_request_with_a_valid_token_and_paired_cookie_is_allowed_through()
    {
        var http = ProtectedHost();
        var (token, cookie) = await MintTokenAsync(http);

        var request = new HttpRequestMessage(HttpMethod.Post, "/g/do");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("RequestVerificationToken", token);
        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("done");
    }

    [Fact]
    public async Task A_valid_token_without_its_paired_cookie_is_rejected()
    {
        var http = ProtectedHost();
        var (token, _) = await MintTokenAsync(http);

        var request = new HttpRequestMessage(HttpMethod.Post, "/g/do");
        request.Headers.Add("RequestVerificationToken", token);
        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the request token alone proves nothing without the double-submit cookie it was minted against");
    }

    [Fact]
    public async Task A_safe_GET_passes_through_the_filter_without_any_token()
    {
        var http = ProtectedHost();

        var response = await http.GetAsync("/g/token");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "GET/HEAD/OPTIONS/TRACE are never state-changing — the filter must not demand a token for them");
    }

    [Fact]
    public void MintRequestVerificationToken_returns_null_when_the_host_registered_no_antiforgery_services()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        var token = WayfinderAntiforgery.MintRequestVerificationToken(context);

        token.Should().BeNull(
            "a bearer-token host that never calls .ValidateWayfinderAntiforgery() pays nothing and its forms carry no token field");
    }
}
