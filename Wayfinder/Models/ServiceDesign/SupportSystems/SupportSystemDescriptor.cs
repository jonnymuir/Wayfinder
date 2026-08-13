using System.Text.Json.Serialization;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Models.ServiceDesign.SupportSystems;

/// <summary>
/// How a support-system capability's asynchronous outcome reaches Wayfinder back. The engine
/// itself always offers both mechanisms as generic, always-on plumbing — a poll-check hook
/// (invoked whenever a client re-polls a waiting stage, reusing the same defer/poll envelope a
/// join gateway already returns) and a generic webhook receiver (resolving an opaque
/// invocation id back to the pending cursor). Which one(s) a given capability actually uses is
/// declared here, per capability — never hardcoded per integration. See
/// docs/guides/support-systems.md.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SupportSystemCompletionMode>))]
public enum SupportSystemCompletionMode
{
    /// <summary>The engine calls the client's status-check method whenever a client re-polls a waiting stage.</summary>
    Poll,

    /// <summary>The engine hands the client a per-invocation callback URL and waits for an inbound call to it.</summary>
    Webhook,
}

/// <summary>
/// One decision or result a capability can resolve to, e.g. <c>"approved"</c>/<c>"rejected"</c> —
/// a closed vocabulary a stage's outgoing route triggers are validated against, so "what did the
/// external system decide" and "which blueprint route fires" are matched against a declared
/// vocabulary rather than an ad-hoc magic string each integration invents for itself.
/// </summary>
public sealed record SupportSystemOutcomeDescriptor
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }
}

/// <summary>
/// One thing a support system can be asked to do, e.g. "validate a risk assessment". Declares
/// its own inputs by reusing <see cref="ComponentPropertyDescriptor"/> — the same recursive
/// shape already shared with action parameters — so a capability input that should be sourced
/// from a blueprint field (<c>Format: "field-ref"</c>) gets the existing reference-aware
/// field-ref editor machinery for free, rather than a bespoke input-authoring UI. A capability
/// with multiple inputs (e.g. a file plus some metadata) just declares more than one.
/// </summary>
public sealed record SupportSystemCapabilityDescriptor
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<ComponentPropertyDescriptor> Inputs { get; init; } = [];

    /// <summary>Which completion mode(s) this capability actually uses — see <see cref="SupportSystemCompletionMode"/>. Must declare at least one.</summary>
    public required IReadOnlyList<SupportSystemCompletionMode> SupportedCompletionModes { get; init; }

    /// <summary>The closed set of outcomes this capability can resolve to. Must declare at least one.</summary>
    public required IReadOnlyList<SupportSystemOutcomeDescriptor> Outcomes { get; init; }
}

/// <summary>
/// A registered external system a blueprint's stages/routes can call out to via a
/// <c>support-system-call</c> action (see <see cref="SupportSystemActionTypes"/>) — Nielsen
/// Norman Group's "support processes" layer of the service-blueprint model
/// (https://www.nngroup.com/articles/service-blueprints-definition/), the third lane alongside
/// a citizen-facing and a caseworker-facing queue. See <see cref="SupportSystemRegistry"/> for
/// how a host registers one, and docs/guides/support-systems.md for the full picture.
/// </summary>
public sealed record SupportSystemDescriptor
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<SupportSystemCapabilityDescriptor> Capabilities { get; init; }
}

/// <summary>
/// The <see cref="ActionDefinition.Type"/> convention that gives that previously-inert record
/// real runtime meaning for the first time: calling out to a registered
/// <see cref="SupportSystemDescriptor"/> capability. <see cref="ActionDefinition.Parameters"/>
/// on an action of this type is expected to carry <c>supportSystemKey</c>, <c>capabilityKey</c>,
/// and an input mapping from each of the capability's declared
/// <see cref="SupportSystemCapabilityDescriptor.Inputs"/> to the blueprint field it's sourced
/// from — the engine's execution of it is Phase 2's job, not this one. See
/// docs/guides/support-systems.md.
/// </summary>
public static class SupportSystemActionTypes
{
    public const string SupportSystemCall = "support-system-call";
}
