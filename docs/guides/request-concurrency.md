# Request concurrency

How a host controls "is there already one?" beyond a blueprint's own declared `requestPolicy`
(`single` | `multiple` | `prompt`, see
[Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) for what each
means at the blueprint-authoring level). This is for whoever is *building* a Wayfinder host, a
blueprint author never needs any of this, they just declare `requestPolicy`.

This document is also exposed as an MCP resource (`service-blueprint-docs://request-concurrency`)
so an agent can fetch it directly without repo access.

---

## The problem this solves

`requestPolicy: "single"` means at most one instance per `(tenantId, userId, blueprintKey)`,
*forever*: once it reaches a terminal stage, ambient `GetCurrent` (no explicit `instanceId`)
keeps returning that same instance on every subsequent visit. That's deliberate: a citizen
revisiting a confirmation page should see "Thank you", not a silently-reset blank form. But it
means a route meant to *start* something, "Submit this month's file", "Apply for another event",
can't reuse the exact same ambient lookup, or it can never actually be used a second time once
the first submission finishes.

Two, independent gaps this closes:

1. **When does "existing" stop counting?**: a distinct "start a new one" affordance needs
   different behaviour from "continue where I left off": reinstate a still-running instance
   (never abandon in-progress work), but genuinely start fresh once the existing one is terminal.
2. **Who does "existing" belong to?**: the default lookup groups by the literal requesting user.
   A host sometimes wants a wider group, e.g. "only one bulk load per organisation running at a
   time", regardless of which of that organisation's users actually submits it.

Neither needed a blueprint-authored concept to solve, and (deliberately) neither is expressed
as one: `ServiceRequest.FieldValues` is empty until an instance's first submission, but both of
these decisions happen *before* any submission exists, so there's nothing to resolve a
blueprint-declared field-ref against yet.

## `GetCurrentOrStartFresh`: a distinct "start" affordance

```csharp
ServiceRequestResponseEnvelope GetCurrentOrStartFresh(
    string blueprintKey, string tenantId, string userId, ActorProfile accessProfile);
```

Call this from whichever route is genuinely a "start a new one" link, not from an ambient
"continue where I left off" entry point (leave those calling plain `GetCurrent`, unchanged).
Reinstates a non-terminal existing instance exactly as ambient `GetCurrent` already does; once the
existing instance is terminal, this is equivalent to the existing explicit
`action: "start-new"`, which already existed and is unconditional (always fresh, no matter what),
used elsewhere by `ServiceBlueprintSimulationRunner` where that's genuinely what's wanted. That's
why this needed to be its own method rather than a change to what `"start-new"` itself means.

Composes correctly with all three request policies with no special-casing needed: a harmless
no-op wrapper for `multiple` (already always fresh either way); for `prompt`, a non-terminal
existing instance still returns `instance_picker` exactly as before (this method's own terminal
check never fires for it), `instance_picker` is a real, engine-supported response state, but has
no host-side rendering built anywhere in `Wayfinder.ReferenceApp` yet; build one if you actually
need the "ask, don't decide" experience.

**A completed instance stops being ambiently reachable once this is used.** If nothing else in
your host surfaces a way to browse past instances (the reference app doesn't, today), switching a
route to this method trades "the old confirmation is the only thing this link can ever show again"
for "you can actually start a new one", know which one your users need before switching.

## `ActorProfile.ConcurrencyScopeKey`: scoping "existing" beyond one user

```csharp
public string? ConcurrencyScopeKey { get; init; } // on ActorProfile
```

Null (the default) reproduces today's exact per-user behaviour for every existing caller. Set it
when building the `ActorProfile` for a route that wants "is there already one?" grouped by
something other than the literal requester, resolved the same way you already resolve
`tenantId`/`userId` themselves (a claim, a lookup, static config):

```csharp
public static ActorProfile NjfOperationsProfile() => CaseworkerProfile() with
{
    ConcurrencyScopeKey = "njf-contributions-org:njf"
};
```

Two different users sharing this key are now treated as one owner for `single`/`prompt` purposes,
regardless of which of them actually submits. Attribution isn't lost, `ServiceRequest.UserId`
still always records who actually created each instance; `ConcurrencyScopeKey` is a separate field
on the instance, defaulting to `userId` at creation when nothing overrides it.

## `IRequestConcurrencyPolicy`: an escape hatch for rules a scope key can't express

For anything a single grouping key can't capture, a blackout window, a check against another
system, a rule spanning more than one blueprint, register a custom policy, mirroring
`ISupportSystemClient`'s own per-key registry shape:

```csharp
public interface IRequestConcurrencyPolicy
{
    IReadOnlyList<string> DefinitionKeys { get; }

    Task<RequestConcurrencyDecision> EvaluateAsync(
        ServiceBlueprint definition, string tenantId, string userId, ActorProfile accessProfile,
        IReadOnlyList<ServiceRequest> candidateInstances, // pre-filtered to this tenant+blueprint
        CancellationToken ct = default);
}
```

Returns `AllowNew`, `ReuseExisting(instance)`, or `Deny(reason)` (`RequestConcurrencyDecision`'s
own static factories). Registered via `ProcessManagerEngine`'s constructor
(`IEnumerable<IRequestConcurrencyPolicy>? requestConcurrencyPolicies`), alongside your other DI
wiring, a blueprint with nothing registered for it is completely untouched, falling straight
through to the built-in `single`/`multiple`/`prompt` (+ `ConcurrencyScopeKey`) logic above. `Deny`
surfaces as an ordinary error envelope (`CONCURRENCY_POLICY_DENIED`), not a new response state.

Most needs don't require this, try `ConcurrencyScopeKey` first.
