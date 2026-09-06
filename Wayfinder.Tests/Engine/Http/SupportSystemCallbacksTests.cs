using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Http;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// <see cref="SupportSystemCallbacks.MapWebhookSupportSystemCallbacks"/> — the inbound callback a
/// webhook support-system consumer posts to resolve a waiting cursor. This is an
/// integrity-critical boundary (forging it skips whatever human step the consumer's automation
/// performs), so the security-relevant behaviours each get their own test. See
/// docs/guides/support-systems.md.
/// </summary>
public class SupportSystemCallbacksTests
{
    private const string Secret = "callback-shared-secret";
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string DefinitionKey = "cb-test";

    private static readonly ActorProfile Caseworker = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "cb-test",
          "displayName": "Callback test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "single",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" },
            { "key": "automation", "displayName": "Automation", "actor": "system" }
          ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "applicantName", "label": "Name", "required": false } ],
              "routes": [ { "id": "start--send--split", "target": "to-support-system", "trigger": "send" } ]
            },
            {
              "stageKey": "in-review",
              "displayName": "In review",
              "queueKey": "automation",
              "components": [ { "type": "panel", "heading": "In review" } ],
              "actions": [
                {
                  "type": "support-system-call",
                  "timing": "onEnter",
                  "params": {
                    "supportSystemKey": "njf-coaching-standards",
                    "capabilityKey": "check-coaching-standards",
                    "inputs": { "applicantName": "applicantName" }
                  }
                }
              ],
              "routes": [
                { "id": "in-review--accredited--join", "target": "check-complete", "trigger": "accredited" },
                { "id": "in-review--referred--join", "target": "check-complete", "trigger": "referred" }
              ]
            },
            { "stageKey": "accredited", "displayName": "Accredited", "queueKey": "caseworker", "components": [ { "type": "panel", "heading": "Accredited" } ] },
            { "stageKey": "referred", "displayName": "Referred", "queueKey": "caseworker", "components": [ { "type": "panel", "heading": "Referred" } ] }
          ],
          "gateways": [
            {
              "key": "to-support-system",
              "displayName": "Send",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [
                { "id": "split--join", "target": "check-complete", "trigger": "send" },
                { "id": "split--review", "target": "in-review", "trigger": "send" }
              ]
            },
            {
              "key": "check-complete",
              "displayName": "Check complete",
              "gatewayType": "Join",
              "queueKey": "caseworker",
              "waitingContent": "Waiting for the coaching-standards decision.",
              "routes": [
                { "id": "join--accredited", "target": "accredited", "trigger": "accredited" },
                { "id": "join--referred", "target": "referred", "trigger": "referred" }
              ],
              "requiredIncomingQueues": ["caseworker", "automation"]
            }
          ]
        }
        """;

    private sealed class StubClient : ISupportSystemClient
    {
        public string SupportSystemKey => "njf-coaching-standards";
        public string? LastInvocationId { get; private set; }

        public Task<SupportSystemInvocationReceipt> InvokeAsync(
            string capabilityKey, IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
            SupportSystemInvocationContext context, CancellationToken ct = default)
        {
            LastInvocationId = context.InvocationId;
            return Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = context.InvocationId });
        }

        public Task<SupportSystemOutcome?> CheckStatusAsync(
            string capabilityKey, SupportSystemInvocationReceipt receipt, CancellationToken ct = default) =>
            Task.FromResult<SupportSystemOutcome?>(null);
    }

    private static (ProcessManagerEngine Engine, string InvocationId) BuildWaitingEngine()
    {
        SupportSystemRegistry.ResetForTests();
        SupportSystemRegistry.Register(new SupportSystemDescriptor
        {
            Key = "njf-coaching-standards",
            DisplayName = "NJF Coaching Standards",
            Capabilities =
            [
                new SupportSystemCapabilityDescriptor
                {
                    Key = "check-coaching-standards",
                    DisplayName = "Check coaching standards",
                    Inputs = [new() { Key = "applicantName", Title = "Applicant name", ValueKind = ComponentPropertyValueKind.String }],
                    Outputs = [new() { Key = "coachingStandardsNote", Title = "Note", ValueKind = ComponentPropertyValueKind.String }],
                    SupportedCompletionModes = [SupportSystemCompletionMode.Webhook],
                    Outcomes = [new() { Key = "accredited", DisplayName = "Accredited" }, new() { Key = "referred", DisplayName = "Referred" }],
                },
            ],
        });

        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(
            BlueprintJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var client = new StubClient();
        var engine = new ProcessManagerEngine(
            NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), supportSystemClients: [client]);

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, Caseworker);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, Caseworker);
        engine.Advance(started.InstanceId, TenantId, UserId, Caseworker, "send", pickedUp.StateVersion,
            new Dictionary<string, object?> { ["applicantName"] = "Ada Juggler" });

        client.LastInvocationId.Should().NotBeNull("the onEnter support-system-call should have run");
        return (engine, client.LastInvocationId!);
    }

    /// <param name="remoteIp">
    /// <see cref="System.Net.IPAddress.Loopback"/> by default — <c>TestServer</c>'s in-memory
    /// transport sets no <c>RemoteIpAddress</c> of its own, so this middleware stands in for a
    /// real socket's peer address, letting tests simulate both a genuine loopback caller and one
    /// that isn't.
    /// </param>
    private static HttpClient Server(ProcessManagerEngine engine, string? secret, System.Net.IPAddress? remoteIp = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => { s.AddLogging(); s.AddRouting(); })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = remoteIp ?? System.Net.IPAddress.Loopback;
                        await next();
                    });
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapWebhookSupportSystemCallbacks(engine, sharedSecret: secret));
                }))
            .Start();
        return host.GetTestClient();
    }

    [Fact]
    public async Task ARejectedSecret_Returns401_AndDoesNotResolveTheInvocation()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, Secret);

        var noHeader = await http.PostAsJsonAsync($"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });
        noHeader.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        http.DefaultRequestHeaders.Add("X-Webhook-Secret", "wrong");
        var wrongHeader = await http.PostAsJsonAsync($"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });
        wrongHeader.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Still pending — a later legitimate callback still works.
        http.DefaultRequestHeaders.Remove("X-Webhook-Secret");
        http.DefaultRequestHeaders.Add("X-Webhook-Secret", Secret);
        var ok = await http.PostAsJsonAsync($"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AValidCallback_ResolvesTheOutcome_AndAdvancesTheWaitingCursor()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, Secret);
        http.DefaultRequestHeaders.Add("X-Webhook-Secret", Secret);

        var response = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}",
            new { outcomeKey = "accredited", resultPayload = new { coachingStandardsNote = "Auto-accredited." } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("resolved");

        var current = engine.GetCurrent(DefinitionKey, TenantId, UserId, Caseworker);
        current.Render!.StateDisplayName.Should().Be("Accredited");
        current.InstanceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AReplayedCallback_IsANoOp_AndDoesNotAdvanceASecondTime()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, Secret);
        http.DefaultRequestHeaders.Add("X-Webhook-Secret", Secret);
        var url = $"/wayfinder/support-systems/callbacks/{invocationId}";

        var first = await http.PostAsJsonAsync(url, new { outcomeKey = "accredited" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var replayReferred = await http.PostAsJsonAsync(url, new { outcomeKey = "referred" });
        replayReferred.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replayReferred.Content.ReadAsStringAsync()).Should().Contain("no-op");

        // The first outcome stands — the replay with a different outcome changed nothing.
        engine.GetCurrent(DefinitionKey, TenantId, UserId, Caseworker).Render!.StateDisplayName.Should().Be("Accredited");
    }

    [Fact]
    public async Task AnUndeclaredOutcomeKey_Returns400_AndDoesNotResolve()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, Secret);
        http.DefaultRequestHeaders.Add("X-Webhook-Secret", Secret);

        var bogus = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "definitely-not-a-real-outcome" });
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stillWorks = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "referred" });
        stillWorks.StatusCode.Should().Be(HttpStatusCode.OK);
        engine.GetCurrent(DefinitionKey, TenantId, UserId, Caseworker).Render!.StateDisplayName.Should().Be("Referred");
    }

    [Fact]
    public async Task WithNoSharedSecretConfigured_TheEndpointStillFunctions_ForALoopbackCaller()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, secret: null, remoteIp: System.Net.IPAddress.Loopback);

        var response = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithNoSharedSecretConfigured_ANonLoopbackCaller_Returns403_AndDoesNotResolveTheInvocation()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var http = Server(engine, secret: null, remoteIp: System.Net.IPAddress.Parse("203.0.113.7"));

        var response = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "with no shared secret configured, only a genuine loopback caller may resolve an invocation — " +
            "the old behaviour ('any caller that reaches it') is exactly the gap this closes");
    }

    [Fact]
    public async Task TheLazyOverload_DoesNotResolveTheEngineUntilTheFirstCallback()
    {
        var (engine, invocationId) = BuildWaitingEngine();
        var resolved = 0;

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => { s.AddLogging(); s.AddRouting(); })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapWebhookSupportSystemCallbacks(
                        () => { resolved++; return engine; }, sharedSecret: Secret));
                }))
            .Start();

        resolved.Should().Be(0, "mapping the route must not resolve the engine");

        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Webhook-Secret", Secret);
        var response = await http.PostAsJsonAsync(
            $"/wayfinder/support-systems/callbacks/{invocationId}", new { outcomeKey = "accredited" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        resolved.Should().Be(1, "the engine is resolved on the first callback");
    }
}
