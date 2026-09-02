using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Configuration;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.SupportSystems;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// The configuration-only webhook support system: a host declares the whole thing — descriptor
/// and outbound endpoint — in <c>Wayfinder:SupportSystems</c>, with no per-integration C#. See
/// docs/guides/support-systems.md.
/// </summary>
public class ConfiguredWebhookSupportSystemTests
{
    private const string Key = "njf-coaching-standards";

    private static IConfiguration Config(Dictionary<string, string?> overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Wayfinder:SupportSystems:0:key"] = Key,
            ["Wayfinder:SupportSystems:0:displayName"] = "NJF Coaching Standards",
            ["Wayfinder:SupportSystems:0:endpoint:url"] = "https://automate.example.test/umbraco/automate/webhook/abc",
            ["Wayfinder:SupportSystems:0:endpoint:auth:type"] = "hmac-sha256",
            ["Wayfinder:SupportSystems:0:endpoint:auth:secretRef"] = "AUTOMATE_SIGNING_KEY",
            ["AUTOMATE_SIGNING_KEY"] = "s3cr3t-signing-key",
            ["Wayfinder:SupportSystems:0:capabilities:0:key"] = "check-coaching-standards",
            ["Wayfinder:SupportSystems:0:capabilities:0:displayName"] = "Check coaching standards",
            ["Wayfinder:SupportSystems:0:capabilities:0:completionModes:0"] = "Webhook",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:0:key"] = "applicantName",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:0:valueKind"] = "String",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:0:format"] = "field-ref",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:0:required"] = "true",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:1:key"] = "yearsCoaching",
            ["Wayfinder:SupportSystems:0:capabilities:0:inputs:1:valueKind"] = "Integer",
            ["Wayfinder:SupportSystems:0:capabilities:0:outputs:0:key"] = "coachingStandardsNote",
            ["Wayfinder:SupportSystems:0:capabilities:0:outputs:0:valueKind"] = "String",
            ["Wayfinder:SupportSystems:0:capabilities:0:outcomes:0:key"] = "accredited",
            ["Wayfinder:SupportSystems:0:capabilities:0:outcomes:1:key"] = "referred",
        };

        foreach (var (k, v) in overrides)
        {
            settings[k] = v;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void AddConfiguredSupportSystems_RegistersADescriptorAndAKeyedClientFromConfigurationAlone()
    {
        SupportSystemRegistry.ResetForTests();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddConfiguredSupportSystems(Config([]));

        var descriptor = SupportSystemRegistry.Find(Key);
        descriptor.Should().NotBeNull();
        descriptor!.DisplayName.Should().Be("NJF Coaching Standards");
        var capability = descriptor.Capabilities.Should().ContainSingle().Subject;
        capability.Key.Should().Be("check-coaching-standards");
        capability.Inputs.Select(i => i.Key).Should().Equal("applicantName", "yearsCoaching");
        capability.Inputs[0].Required.Should().BeTrue();
        capability.Inputs[0].Format.Should().Be("field-ref");
        capability.Inputs[1].ValueKind.Should().Be(ComponentPropertyValueKind.Integer);
        capability.Outputs.Should().ContainSingle(o => o.Key == "coachingStandardsNote");
        capability.SupportedCompletionModes.Should().Equal(SupportSystemCompletionMode.Webhook);
        capability.Outcomes.Select(o => o.Key).Should().Equal("accredited", "referred");

        var provider = services.BuildServiceProvider();
        var client = provider.GetServices<ISupportSystemClient>().Should().ContainSingle().Subject;
        client.Should().BeOfType<WebhookSupportSystemClient>();
        client.SupportSystemKey.Should().Be(Key);
    }

    [Fact]
    public void AddConfiguredSupportSystems_IsANoOp_WhenTheSectionIsAbsent()
    {
        SupportSystemRegistry.ResetForTests();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddConfiguredSupportSystems(new ConfigurationBuilder().Build());

        services.BuildServiceProvider().GetServices<ISupportSystemClient>().Should().BeEmpty();
        SupportSystemRegistry.All.Should().BeEmpty();
    }

    [Fact]
    public void AddConfiguredSupportSystems_IsIdempotent()
    {
        SupportSystemRegistry.ResetForTests();
        var services = new ServiceCollection();
        services.AddLogging();
        var config = Config([]);

        services.AddConfiguredSupportSystems(config);
        services.AddConfiguredSupportSystems(config);

        services.BuildServiceProvider().GetServices<ISupportSystemClient>().Should().ContainSingle();
    }

    [Fact]
    public void AddConfiguredSupportSystems_Throws_WhenAnHmacSecretRefPointsAtAnUnsetKey()
    {
        SupportSystemRegistry.ResetForTests();
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddConfiguredSupportSystems(
            Config(new() { ["AUTOMATE_SIGNING_KEY"] = null }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*secretRef*");
    }

    // ─── WebhookSupportSystemClient HTTP behaviour ────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatus);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static WebhookSupportSystemEndpoint Endpoint(string authType = "hmac-sha256", string? secret = "s3cr3t-signing-key") => new()
    {
        SupportSystemKey = Key,
        Url = new Uri("https://automate.example.test/umbraco/automate/webhook/abc"),
        Method = HttpMethod.Post,
        AuthType = authType,
        AuthSecret = secret,
        AuthHeaderName = authType == "hmac-sha256" ? "X-Webhook-Signature" : "X-Webhook-Secret",
        HttpClientName = "test",
    };

    private static SupportSystemInvocationContext Context() => new()
    {
        InstanceId = "instance-1",
        InvocationId = "inv-abc123",
        WebhookExpected = true,
    };

    [Fact]
    public async Task InvokeAsync_PostsAnEnvelopeCarryingInvocationIdAndScalarInputs_ButNeverACallbackUrl()
    {
        var handler = new RecordingHandler();
        var client = new WebhookSupportSystemClient(
            Endpoint(), new SingleClientFactory(new HttpClient(handler)), NullLogger<WebhookSupportSystemClient>.Instance);

        var receipt = await client.InvokeAsync(
            "check-coaching-standards",
            new Dictionary<string, SupportSystemInputValue>
            {
                ["applicantName"] = SupportSystemInputValue.Resolve("Ada Juggler"),
                ["yearsCoaching"] = SupportSystemInputValue.Resolve(1),
            },
            Context());

        receipt.ExternalReference.Should().Be("inv-abc123");

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        body["invocationId"]!.GetValue<string>().Should().Be("inv-abc123");
        body["instanceId"]!.GetValue<string>().Should().Be("instance-1");
        body["supportSystemKey"]!.GetValue<string>().Should().Be(Key);
        body["capabilityKey"]!.GetValue<string>().Should().Be("check-coaching-standards");
        body["inputs"]!["applicantName"]!.GetValue<string>().Should().Be("Ada Juggler");
        body["inputs"]!["yearsCoaching"]!.GetValue<int>().Should().Be(1);
        body.ContainsKey("callbackUrl").Should().BeFalse();
        handler.LastBody!.Should().NotContain("callback", "the consumer owns its own callback target");
    }

    [Fact]
    public async Task InvokeAsync_SignsTheBodyWithHmacSha256_InTheGitHubStyleHeader()
    {
        var handler = new RecordingHandler();
        var client = new WebhookSupportSystemClient(
            Endpoint(), new SingleClientFactory(new HttpClient(handler)), NullLogger<WebhookSupportSystemClient>.Instance);

        await client.InvokeAsync("check-coaching-standards", new Dictionary<string, SupportSystemInputValue>(), Context());

        var header = handler.LastRequest!.Headers.GetValues("X-Webhook-Signature").Single();
        header.Should().StartWith("sha256=");
        var expected = "sha256=" + Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("s3cr3t-signing-key"),
                System.Text.Encoding.UTF8.GetBytes(handler.LastBody!)));
        header.Should().Be(expected);
    }

    [Fact]
    public async Task InvokeAsync_SendsAPlainSharedSecretHeader_WhenAuthTypeIsHeader()
    {
        var handler = new RecordingHandler();
        var client = new WebhookSupportSystemClient(
            Endpoint("header", "plain-token"), new SingleClientFactory(new HttpClient(handler)),
            NullLogger<WebhookSupportSystemClient>.Instance);

        await client.InvokeAsync("check-coaching-standards", new Dictionary<string, SupportSystemInputValue>(), Context());

        handler.LastRequest!.Headers.GetValues("X-Webhook-Secret").Single().Should().Be("plain-token");
    }

    [Fact]
    public async Task InvokeAsync_Throws_WhenAnInputResolvesToAnUploadedFile()
    {
        var client = new WebhookSupportSystemClient(
            Endpoint(), new SingleClientFactory(new HttpClient(new RecordingHandler())),
            NullLogger<WebhookSupportSystemClient>.Instance);

        var fileValue = SupportSystemInputValue.Resolve(JsonSerializer.SerializeToNode(new ServiceRequestFileReference
        {
            StorageKey = "k", OriginalFileName = "risk.pdf", ContentType = "application/pdf", SizeBytes = 10,
        }));
        fileValue.FileReference.Should().NotBeNull("guards the test's own premise");

        var act = () => client.InvokeAsync(
            "check-coaching-standards",
            new Dictionary<string, SupportSystemInputValue> { ["file"] = fileValue },
            Context());

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*scalar inputs only*");
    }

    [Fact]
    public async Task InvokeAsync_Throws_WhenTheEndpointReturnsAnErrorStatus()
    {
        var handler = new RecordingHandler { ResponseStatus = HttpStatusCode.InternalServerError };
        var client = new WebhookSupportSystemClient(
            Endpoint(), new SingleClientFactory(new HttpClient(handler)), NullLogger<WebhookSupportSystemClient>.Instance);

        var act = () => client.InvokeAsync("check-coaching-standards", new Dictionary<string, SupportSystemInputValue>(), Context());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*500*");
    }
}
