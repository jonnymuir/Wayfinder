using System.Text.Json.Nodes;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// A capability input's resolved value — whatever <see cref="ProcessManagerEngine"/> found in
/// <c>ServiceRequest.FieldValues</c> for the blueprint field a <c>support-system-call</c>
/// action's input mapping points at. <see cref="FileReference"/> is populated instead of
/// <see cref="RawValue"/> when the field held a <c>file-upload</c> value (detected via
/// <see cref="ServiceRequestFileReference.FromFieldValue"/>) — a client reads bytes itself via
/// its own host-registered <c>IServiceRequestFileStorage</c>, the same way any other host code
/// does; the engine never touches file bytes.
/// </summary>
public sealed record SupportSystemInputValue
{
    public object? RawValue { get; init; }

    public ServiceRequestFileReference? FileReference { get; init; }

    public static SupportSystemInputValue Resolve(object? raw) =>
        ServiceRequestFileReference.FromFieldValue(raw) is { } fileReference
            ? new SupportSystemInputValue { RawValue = raw, FileReference = fileReference }
            : new SupportSystemInputValue { RawValue = raw };
}

/// <summary>
/// What an <see cref="ISupportSystemClient"/> needs to know about the invocation it's starting,
/// beyond the capability's own declared inputs.
/// </summary>
public sealed record SupportSystemInvocationContext
{
    public required string InstanceId { get; init; }

    /// <summary>
    /// Opaque correlation token the engine generated for this invocation. If the capability
    /// declares <see cref="SupportSystems.SupportSystemCompletionMode.Webhook"/>
    /// (<see cref="WebhookExpected"/>), a client that needs the external system to call back
    /// should tell it to address that call by this id — see
    /// <c>Wayfinder.Engine.Api</c>'s generic support-system webhook receiver. The engine itself
    /// has no notion of its own public base URL (that's a host/deployment concern), so building
    /// an actual callback URL from this id is the client's job, not the engine's.
    /// </summary>
    public required string InvocationId { get; init; }

    /// <summary>
    /// True when the capability declared <see cref="SupportSystems.SupportSystemCompletionMode.Webhook"/>
    /// support — the client should register <see cref="InvocationId"/> as a correlation token
    /// with the external system. False means only polling will ever be used for this invocation;
    /// the client need not tell the external system about any callback at all.
    /// </summary>
    public required bool WebhookExpected { get; init; }
}

/// <summary>
/// What starting an invocation returns — just enough for the engine to ask about it again later
/// via <see cref="ISupportSystemClient.CheckStatusAsync"/>, if the capability supports polling.
/// </summary>
public sealed record SupportSystemInvocationReceipt
{
    /// <summary>Opaque, client-defined correlation token against the external system (e.g. their own submission id).</summary>
    public string ExternalReference { get; init; } = "";
}

/// <summary>
/// A capability call's resolved result — one of the capability's declared
/// <see cref="SupportSystems.SupportSystemOutcomeDescriptor"/> keys, plus whatever extra data
/// the external system returned that the blueprint wants merged into the instance's field
/// values (e.g. a decision note) once the matching outgoing route fires.
/// </summary>
public sealed record SupportSystemOutcome
{
    public required string OutcomeKey { get; init; }

    public JsonObject? ResultPayload { get; init; }
}

/// <summary>
/// A host's connection to one registered <see cref="SupportSystems.SupportSystemDescriptor"/> —
/// the thing that actually talks to the external system. Register one per
/// <see cref="SupportSystems.SupportSystemDescriptor.Key"/> alongside the descriptor itself; see
/// docs/guides/support-systems.md.
/// </summary>
public interface ISupportSystemClient
{
    /// <summary>Must match a registered <see cref="SupportSystems.SupportSystemDescriptor.Key"/>.</summary>
    string SupportSystemKey { get; }

    /// <summary>
    /// Starts a capability call. Called once, synchronously, when a stage carrying a
    /// <c>support-system-call</c> action with <c>Timing: "onEnter"</c> is entered — the engine
    /// blocks on this (a deliberate tradeoff: the whole engine is synchronous today, and this
    /// call is expected to be fast — enqueue-and-return, not do-the-work-and-return). Must not
    /// throw for an expected business outcome (e.g. the external system rejecting the request
    /// outright) — return that as an immediately-known <see cref="SupportSystemOutcome"/> via
    /// <see cref="CheckStatusAsync"/> on the very next poll instead, or throw only for genuine
    /// infrastructure failure.
    /// </summary>
    Task<SupportSystemInvocationReceipt> InvokeAsync(
        string capabilityKey,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Asks whether an invocation has resolved yet. Only ever called for a capability that
    /// declared <see cref="SupportSystems.SupportSystemCompletionMode.Poll"/> — a client whose
    /// capabilities are all webhook-only never needs a real implementation of this. Returns
    /// <see langword="null"/> while still pending.
    /// </summary>
    Task<SupportSystemOutcome?> CheckStatusAsync(
        string capabilityKey,
        SupportSystemInvocationReceipt receipt,
        CancellationToken ct = default);
}
