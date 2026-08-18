using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// What an <see cref="IRequestConcurrencyPolicy"/> decides, given the candidate instances already
/// sharing this request's tenant and blueprint.
/// </summary>
public enum RequestConcurrencyOutcome
{
    /// <summary>Create a genuinely new instance, same as if none of the candidates existed.</summary>
    AllowNew,

    /// <summary>Return <see cref="RequestConcurrencyDecision.ExistingInstance"/> instead of creating a new one.</summary>
    ReuseExisting,

    /// <summary>Refuse outright — surfaces as an error, not a render.</summary>
    Deny
}

/// <summary>See <see cref="RequestConcurrencyOutcome"/> for what each case means.</summary>
public sealed record RequestConcurrencyDecision
{
    public required RequestConcurrencyOutcome Outcome { get; init; }

    /// <summary>Required when <see cref="Outcome"/> is <see cref="RequestConcurrencyOutcome.ReuseExisting"/>.</summary>
    public ServiceRequest? ExistingInstance { get; init; }

    /// <summary>Shown to the caller when <see cref="Outcome"/> is <see cref="RequestConcurrencyOutcome.Deny"/>.</summary>
    public string? DenyReason { get; init; }

    public static RequestConcurrencyDecision AllowNew() => new() { Outcome = RequestConcurrencyOutcome.AllowNew };

    public static RequestConcurrencyDecision ReuseExisting(ServiceRequest instance) =>
        new() { Outcome = RequestConcurrencyOutcome.ReuseExisting, ExistingInstance = instance };

    public static RequestConcurrencyDecision Deny(string reason) =>
        new() { Outcome = RequestConcurrencyOutcome.Deny, DenyReason = reason };
}

/// <summary>
/// The escape hatch for a concurrency rule <see cref="ActorProfile.ConcurrencyScopeKey"/> can't
/// express — a scope key can only say "group existing instances by this string"; this can express
/// anything else (a blackout window, a check against another system, a rule spanning more than
/// one blueprint). Most needs don't require this — see <c>ActorProfile.ConcurrencyScopeKey</c>'s
/// own remarks and <c>ProcessManagerEngine.GetCurrentOrStartFresh</c> first.
///
/// Registered per <see cref="DefinitionKeys"/> alongside a host's own DI setup, the same way
/// <see cref="ISupportSystemClient"/> is registered per <see cref="ISupportSystemClient.SupportSystemKey"/>
/// — a blueprint with nothing registered for it falls straight through to the engine's own
/// built-in single/multiple/prompt (+ ConcurrencyScopeKey) behaviour, completely untouched.
/// </summary>
public interface IRequestConcurrencyPolicy
{
    /// <summary>The blueprint definition key(s) this policy overrides the built-in logic for.</summary>
    IReadOnlyList<string> DefinitionKeys { get; }

    /// <summary>
    /// Called instead of the engine's own built-in lookup, for the blueprints named in
    /// <see cref="DefinitionKeys"/> only. <paramref name="candidateInstances"/> is pre-filtered to
    /// this tenant and blueprint — the policy only needs to implement the genuinely bespoke part
    /// of the decision, not re-derive that basic filter itself.
    /// </summary>
    Task<RequestConcurrencyDecision> EvaluateAsync(
        ServiceBlueprint definition,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        IReadOnlyList<ServiceRequest> candidateInstances,
        CancellationToken ct = default);
}
