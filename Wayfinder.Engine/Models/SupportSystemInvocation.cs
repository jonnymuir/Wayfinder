using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Models;

/// <summary>
/// One in-flight (or resolved) call to a support-system capability, tracked on the instance that
/// started it. Created when a stage's <c>onEnter</c> <c>support-system-call</c> action executes;
/// resolved by either the poll-check hook (<see cref="Services.ProcessManagerEngine"/>'s own
/// re-check on every <c>GetCurrent</c> against a waiting join gateway) or the generic webhook
/// receiver, whichever the capability's declared completion mode(s) end up delivering first.
/// </summary>
public sealed record SupportSystemInvocation
{
    /// <summary>Opaque correlation token — the only thing a webhook callback carries to identify which invocation it resolves.</summary>
    public required string InvocationId { get; init; }

    public required string SupportSystemKey { get; init; }

    public required string CapabilityKey { get; init; }

    /// <summary>The cursor that was waiting on this invocation when it started.</summary>
    public required string CursorId { get; init; }

    /// <summary>The stage this invocation's <c>onEnter</c> action ran on.</summary>
    public required string StageKey { get; init; }

    public SupportSystemInvocationReceipt? Receipt { get; init; }

    /// <summary>
    /// True once an outcome has been delivered (poll or webhook) and consumed. Checked before
    /// acting on a second delivery for the same invocation — a capability declaring both
    /// completion modes may have both arrive; only the first should ever advance anything.
    /// </summary>
    public bool Resolved { get; init; }

    public string? OutcomeKey { get; init; }
}
