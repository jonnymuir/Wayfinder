using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Configuration;
using Wayfinder.Engine.SupportSystems;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Extensions;

/// <summary>
/// Wires up support systems declared entirely in the <c>Wayfinder:SupportSystems</c>
/// configuration section — no per-integration C#. Each entry registers a
/// <see cref="SupportSystemDescriptor"/> in <see cref="SupportSystemRegistry"/> and a keyed
/// <see cref="WebhookSupportSystemClient"/> that POSTs invocations to the configured URL. The
/// inbound half (the callback that resolves an outcome) is <c>MapWebhookSupportSystemCallbacks</c>
/// in <c>Wayfinder.Engine.Http</c>. See docs/guides/support-systems.md.
/// </summary>
public static class SupportSystemServiceCollectionExtensions
{
    private sealed class ConfiguredSupportSystemsMarker;

    /// <summary>
    /// Binds <c>Wayfinder:SupportSystems</c> and, for each entry, calls
    /// <see cref="SupportSystemRegistry.Register"/> (synchronously — so it runs before the engine
    /// reads any blueprint) and registers an <see cref="ISupportSystemClient"/> plus its named
    /// <see cref="System.Net.Http.HttpClient"/>. A no-op when the section is absent or empty, and
    /// idempotent: calling it more than once does nothing the second time.
    /// </summary>
    public static IServiceCollection AddConfiguredSupportSystems(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(ConfiguredSupportSystemsMarker)))
        {
            return services;
        }

        services.AddSingleton<ConfiguredSupportSystemsMarker>();

        var entries = configuration.GetSection(WebhookSupportSystemOptions.SectionName)
            .Get<List<WebhookSupportSystemOptions>>() ?? [];

        foreach (var entry in entries)
        {
            var endpoint = BuildEndpoint(entry, configuration);

            SupportSystemRegistry.Register(ToDescriptor(entry));

            services.AddHttpClient(endpoint.HttpClientName, client =>
            {
                // InvokeAsync is expected to be enqueue-and-return; a slow downstream must not
                // pin the (synchronous) engine call indefinitely.
                client.Timeout = TimeSpan.FromSeconds(15);
            });

            services.AddSingleton<ISupportSystemClient>(sp => new WebhookSupportSystemClient(
                endpoint,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookSupportSystemClient>>()));
        }

        return services;
    }

    private static WebhookSupportSystemEndpoint BuildEndpoint(
        WebhookSupportSystemOptions entry, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            throw new InvalidOperationException(
                $"A '{WebhookSupportSystemOptions.SectionName}' entry has no 'key'.");
        }

        if (!Uri.TryCreate(entry.Endpoint.Url, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException(
                $"Support system '{entry.Key}' has no valid absolute 'endpoint.url'.");
        }

        var auth = entry.Endpoint.Auth;
        var authType = (auth?.Type ?? "none").ToLowerInvariant();
        var authSecret = auth?.SecretRef is { Length: > 0 } secretRef ? configuration[secretRef] : null;

        if (authType is "hmac-sha256" or "header" && string.IsNullOrEmpty(authSecret))
        {
            throw new InvalidOperationException(
                $"Support system '{entry.Key}' declares '{authType}' outbound auth but configuration key " +
                $"'{auth?.SecretRef}' (endpoint.auth.secretRef) is not set.");
        }

        var headerName = auth?.HeaderName is { Length: > 0 } explicitHeader
            ? explicitHeader
            : authType switch
            {
                "hmac-sha256" => "X-Webhook-Signature",
                "header" => "X-Webhook-Secret",
                _ => "X-Webhook-Secret",
            };

        return new WebhookSupportSystemEndpoint
        {
            SupportSystemKey = entry.Key,
            Url = url,
            Method = new HttpMethod(string.IsNullOrWhiteSpace(entry.Endpoint.Method) ? "POST" : entry.Endpoint.Method),
            AuthType = authType,
            AuthSecret = authSecret,
            AuthHeaderName = headerName,
            HttpClientName = $"wayfinder-support-system:{entry.Key}",
        };
    }

    private static SupportSystemDescriptor ToDescriptor(WebhookSupportSystemOptions entry) => new()
    {
        Key = entry.Key,
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Key : entry.DisplayName,
        Description = entry.Description,
        Capabilities = entry.Capabilities.Select(ToCapability).ToArray(),
    };

    private static SupportSystemCapabilityDescriptor ToCapability(WebhookSupportSystemCapabilityOptions capability) => new()
    {
        Key = capability.Key,
        DisplayName = string.IsNullOrWhiteSpace(capability.DisplayName) ? capability.Key : capability.DisplayName,
        Description = capability.Description,
        Inputs = capability.Inputs.Select(ToProperty).ToArray(),
        Outputs = capability.Outputs.Select(ToProperty).ToArray(),
        SupportedCompletionModes = capability.CompletionModes.Count > 0
            ? capability.CompletionModes.Distinct().ToArray()
            : [SupportSystemCompletionMode.Webhook],
        Outcomes = capability.Outcomes.Select(o => new SupportSystemOutcomeDescriptor
        {
            Key = o.Key,
            DisplayName = string.IsNullOrWhiteSpace(o.DisplayName) ? o.Key : o.DisplayName,
        }).ToArray(),
    };

    private static ComponentPropertyDescriptor ToProperty(WebhookSupportSystemPropertyOptions property) => new()
    {
        Key = property.Key,
        Title = string.IsNullOrWhiteSpace(property.Title) ? property.Key : property.Title,
        Description = property.Description,
        ValueKind = property.ValueKind,
        Format = property.Format,
        Required = property.Required,
    };
}
