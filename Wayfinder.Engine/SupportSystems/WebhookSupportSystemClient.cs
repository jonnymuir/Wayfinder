using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.SupportSystems;

/// <summary>
/// A fully resolved, immutable outbound endpoint for one configured support system — built by
/// <see cref="Extensions.SupportSystemServiceCollectionExtensions.AddConfiguredSupportSystems"/>
/// from a <see cref="Configuration.WebhookSupportSystemOptions"/> entry, with every
/// <c>*SecretRef</c> already dereferenced against configuration so the client never touches
/// <c>IConfiguration</c> itself.
/// </summary>
public sealed record WebhookSupportSystemEndpoint
{
    public required string SupportSystemKey { get; init; }

    public required Uri Url { get; init; }

    public required HttpMethod Method { get; init; }

    /// <summary><c>hmac-sha256</c>, <c>header</c>, or <c>none</c>.</summary>
    public required string AuthType { get; init; }

    /// <summary>The resolved signing key (hmac) or header value (header). Null for <c>none</c>.</summary>
    public string? AuthSecret { get; init; }

    public required string AuthHeaderName { get; init; }

    /// <summary>The <see cref="IHttpClientFactory"/> name registered for this endpoint.</summary>
    public required string HttpClientName { get; init; }
}

/// <summary>
/// The generic, configuration-driven <see cref="ISupportSystemClient"/>: it POSTs each invocation
/// as a small JSON envelope to a configured URL and lets whatever is on the other end (an Umbraco
/// Automate automation, Zapier, Make, n8n, a bespoke service) do the work and later resolve the
/// outcome by calling back into <c>MapWebhookSupportSystemCallbacks</c>
/// (Wayfinder.Engine.Http) → <see cref="Services.ProcessManagerEngine.ResolveSupportSystemOutcome"/>.
/// Nothing here is specific to any one of those consumers. See docs/guides/support-systems.md.
/// </summary>
/// <remarks>
/// The envelope deliberately carries <c>invocationId</c> but <b>no callback URL</b>: the consumer
/// owns its own callback target as its own configuration. A caller-supplied callback URL would let
/// anyone who can reach this endpoint (e.g. with a leaked webhook secret) turn the host into an
/// HTTP client aimed at an arbitrary address.
/// <para/>
/// A capability input that resolves to an uploaded file
/// (<see cref="SupportSystemInputValue.FileReference"/>) is sent as the
/// <see cref="Wayfinder.Models.ServiceDesign.ServiceRequestFileReference"/> object (storage key
/// and descriptive metadata), the same shape a file-typed output uses on the way back in. No
/// bytes and no URL travel: the key is meaningful to a consumer that shares the host's
/// <see cref="IServiceRequestFileStorage"/> (for example an automation on the same site), and
/// opaque to any other.
/// </remarks>
public sealed partial class WebhookSupportSystemClient(
    WebhookSupportSystemEndpoint endpoint,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookSupportSystemClient> logger) : ISupportSystemClient
{
    public string SupportSystemKey => endpoint.SupportSystemKey;

    public async Task<SupportSystemInvocationReceipt> InvokeAsync(
        string capabilityKey,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct = default)
    {
        var inputObject = new JsonObject();
        foreach (var (key, value) in inputs)
        {
            inputObject[key] = value.FileReference is { } file
                ? JsonSerializer.SerializeToNode(file, JsonSerializerOptions.Web)
                : value.RawValue is null ? null : JsonSerializer.SerializeToNode(value.RawValue);
        }

        var envelope = new JsonObject
        {
            ["invocationId"] = context.InvocationId,
            ["instanceId"] = context.InstanceId,
            ["supportSystemKey"] = SupportSystemKey,
            ["capabilityKey"] = capabilityKey,
            ["inputs"] = inputObject,
        };

        var body = envelope.ToJsonString();

        using var request = new HttpRequestMessage(endpoint.Method, endpoint.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(request, body);

        var client = httpClientFactory.CreateClient(endpoint.HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Surface as an infrastructure failure — ProcessManagerEngine wraps InvokeAsync in
            // try/catch and, on failure, simply does not create the pending invocation, so the
            // stage never enters its wait. That is the right behaviour for "the downstream is
            // unreachable"; an expected business rejection must come back via a resolved outcome.
            throw new InvalidOperationException(
                $"POST to support system '{SupportSystemKey}' endpoint failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Support system '{SupportSystemKey}' endpoint returned {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase} for capability '{capabilityKey}'.");
            }

            CapabilityInvoked(logger, SupportSystemKey, capabilityKey, (int)response.StatusCode, context.InvocationId);
        }

        // A webhook consumer (Automate returns 202 with no useful body) has no external id of its
        // own to hand back yet — we correlate purely by the invocation id, which the consumer
        // echoes on its callback.
        return new SupportSystemInvocationReceipt { ExternalReference = context.InvocationId };
    }

    /// <summary>
    /// Webhook completion only — the outcome arrives via the inbound callback endpoint, never a
    /// poll. A config entry declaring <c>Poll</c> would need a <c>statusUrl</c>, which this client
    /// does not model.
    /// </summary>
    public Task<SupportSystemOutcome?> CheckStatusAsync(
        string capabilityKey,
        SupportSystemInvocationReceipt receipt,
        CancellationToken ct = default) =>
        Task.FromResult<SupportSystemOutcome?>(null);

    private void ApplyAuth(HttpRequestMessage request, string body)
    {
        switch (endpoint.AuthType)
        {
            case "hmac-sha256":
            {
                if (string.IsNullOrEmpty(endpoint.AuthSecret))
                {
                    throw new InvalidOperationException(
                        $"Support system '{SupportSystemKey}' declares hmac-sha256 auth but its signing key " +
                        "resolved to empty — check the SecretRef points at a set configuration key.");
                }

                var hex = Convert.ToHexStringLower(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AuthSecret), Encoding.UTF8.GetBytes(body)));
                request.Headers.TryAddWithoutValidation(endpoint.AuthHeaderName, $"sha256={hex}");
                break;
            }

            case "header":
            {
                if (string.IsNullOrEmpty(endpoint.AuthSecret))
                {
                    throw new InvalidOperationException(
                        $"Support system '{SupportSystemKey}' declares header auth but its secret resolved to " +
                        "empty — check the SecretRef points at a set configuration key.");
                }

                request.Headers.TryAddWithoutValidation(endpoint.AuthHeaderName, endpoint.AuthSecret);
                break;
            }

            case "none":
                NoOutboundAuthentication(logger, SupportSystemKey);
                break;

            default:
                throw new InvalidOperationException(
                    $"Support system '{SupportSystemKey}' has unknown auth type '{endpoint.AuthType}' " +
                    "(expected 'hmac-sha256', 'header', or 'none').");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Support system '{System}' capability '{Capability}' invoked ({Status}); invocation {InvocationId}.")]
    private static partial void CapabilityInvoked(ILogger logger, string system, string capability, int status, string invocationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Support system '{System}' endpoint is configured with no outbound authentication — acceptable only on a trusted network.")]
    private static partial void NoOutboundAuthentication(ILogger logger, string system);
}
