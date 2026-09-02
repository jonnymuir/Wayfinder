using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.Configuration;

/// <summary>
/// One entry in the <c>Wayfinder:SupportSystems</c> configuration array — a whole
/// <see cref="SupportSystemDescriptor"/> plus the outbound HTTP endpoint a
/// <see cref="SupportSystems.WebhookSupportSystemClient"/> POSTs each invocation to. A host
/// wires an Umbraco Automate automation (or Zapier / Make / n8n / a bespoke service) purely by
/// adding one of these to configuration — see
/// <see cref="Extensions.SupportSystemServiceCollectionExtensions.AddConfiguredSupportSystems"/>
/// and docs/guides/support-systems.md.
/// </summary>
public sealed class WebhookSupportSystemOptions
{
    /// <summary>The config section this binds from.</summary>
    public const string SectionName = "Wayfinder:SupportSystems";

    /// <summary>Unique support-system key a blueprint's <c>support-system-call</c> action references.</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? Description { get; set; }

    public WebhookSupportSystemEndpointOptions Endpoint { get; set; } = new();

    public List<WebhookSupportSystemCapabilityOptions> Capabilities { get; set; } = [];
}

/// <summary>The outbound side: where and how this support system's invocations are POSTed.</summary>
public sealed class WebhookSupportSystemEndpointOptions
{
    /// <summary>Absolute URL the invocation envelope is POSTed to (e.g. an Automate webhook trigger URL).</summary>
    public string Url { get; set; } = "";

    public string Method { get; set; } = "POST";

    /// <summary>How the outbound POST authenticates itself to the endpoint. Omit only on a trusted network.</summary>
    public WebhookSupportSystemAuthOptions? Auth { get; set; }

    /// <summary>
    /// Name of the configuration key (env var / user-secret) holding the shared secret the
    /// <em>inbound</em> callback endpoint requires — never the secret value itself (CLAUDE.md
    /// security rule 5). A host passes the resolved value to
    /// <c>MapWebhookSupportSystemCallbacks</c> (Wayfinder.Engine.Http). Purely informational to
    /// the outbound client; kept here so the whole integration reads from one config block.
    /// </summary>
    public string? CallbackSecretRef { get; set; }
}

/// <summary>
/// Outbound authentication for the invocation POST. Defaults match Umbraco Automate's built-in
/// webhook authenticators exactly, so a host needs no extra configuration to interoperate, but
/// they are ordinary webhook conventions, not Automate-specific.
/// </summary>
public sealed class WebhookSupportSystemAuthOptions
{
    /// <summary><c>hmac-sha256</c> (preferred), <c>header</c> (plain shared secret), or <c>none</c>.</summary>
    public string Type { get; set; } = "none";

    /// <summary>
    /// Name of the configuration key (env var / user-secret) holding the signing key
    /// (<c>hmac-sha256</c>) or the header value (<c>header</c>) — never the secret itself.
    /// </summary>
    public string? SecretRef { get; set; }

    /// <summary>
    /// Header to carry the credential. Defaults: <c>X-Webhook-Signature</c> for
    /// <c>hmac-sha256</c> (value <c>sha256=&lt;lowercase-hex&gt;</c> of HMAC-SHA256 over the raw
    /// request body), <c>X-Webhook-Secret</c> for <c>header</c>.
    /// </summary>
    public string? HeaderName { get; set; }
}

/// <summary>Config shape of one <see cref="SupportSystemCapabilityDescriptor"/>.</summary>
public sealed class WebhookSupportSystemCapabilityOptions
{
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? Description { get; set; }

    /// <summary><see cref="SupportSystemCompletionMode.Webhook"/> and/or <see cref="SupportSystemCompletionMode.Poll"/>. Bound by name.</summary>
    public List<SupportSystemCompletionMode> CompletionModes { get; set; } = [];

    public List<WebhookSupportSystemPropertyOptions> Inputs { get; set; } = [];

    public List<WebhookSupportSystemPropertyOptions> Outputs { get; set; } = [];

    public List<WebhookSupportSystemOutcomeOptions> Outcomes { get; set; } = [];
}

/// <summary>Slim config shape mapped onto <see cref="ComponentPropertyDescriptor"/>.</summary>
public sealed class WebhookSupportSystemPropertyOptions
{
    /// <summary>Author-chosen identifier. Must start lowercase (see <see cref="SupportSystemRegistry"/>).</summary>
    public string Key { get; set; } = "";

    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Bound by name (<c>String</c>, <c>Integer</c>, <c>Boolean</c>, …). Defaults to <c>String</c>.</summary>
    public ComponentPropertyValueKind ValueKind { get; set; } = ComponentPropertyValueKind.String;

    /// <summary>Semantic hint, e.g. <c>field-ref</c> for an input sourced from a blueprint field.</summary>
    public string? Format { get; set; }

    public bool Required { get; set; }
}

/// <summary>Config shape of one <see cref="SupportSystemOutcomeDescriptor"/>.</summary>
public sealed class WebhookSupportSystemOutcomeOptions
{
    public string Key { get; set; } = "";

    public string? DisplayName { get; set; }
}
