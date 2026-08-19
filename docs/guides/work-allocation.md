# Work allocation: queue eligibility, claim/ownership, and audit

How Wayfinder models real work-allocation scenarios beyond a static "can this actor see this
queue" check: which team is eligible to work a queue, who currently owns one specific item within
it, an atomic "claim next available" primitive for automated/scaled-out workers, and a full audit
trail of who did what. For whoever is *building* a Wayfinder host — a blueprint author only needs
to know about `RoleGates` (declaring eligibility); nothing else here is authored in blueprint JSON.

This document is also exposed as an MCP resource (`service-blueprint-docs://work-allocation`) so
an agent can fetch it directly without repo access.

---

## Two independent axes

Before this feature, `ActorProfile`'s `VisibleQueues`/`StartableQueues`/`ActionableQueues` were the
whole story: static, per-actor allow-lists — once an actor could act in a queue, every item in it
was equally visible and actionable to them. That's still true for the common case. Two more axes
sit on top of it, and they answer genuinely different questions:

- **Queue eligibility** — *which team* is allowed to see/claim from this queue at all.
- **Claim/ownership** — *which specific person, right now* owns one particular item within a
  queue this actor is already eligible for.

Both apply to any shared (non-owner-restricted) queue, independently of each other and of whether
the other is even in use. A queue with no declared eligibility restriction and nobody ever calling
`ClaimWorkItem` on it behaves exactly as it always has — this is fully backward-compatible.

**A naming collision to know about**: `IQueueCapabilitiesProvider` already used the word
"capability" before this feature existed, for something unrelated — which *component types* a
host can render for a queue. `ActorProfile.Capabilities`/`QueueDefinition.RoleGates` is a
different concept (skill/team eligibility), and deliberately uses the opposite null-vs-empty
convention from `IQueueCapabilitiesProvider`'s own (there, null vs. empty is meaningfully
distinguished; here, null and empty both simply mean "unrestricted"). Don't confuse the two.

## Queue eligibility — `QueueDefinition.RoleGates` / `ActorProfile.Capabilities`

```csharp
// QueueDefinition (blueprint-authored)
public IReadOnlyList<string>? RoleGates { get; init; } // null/empty = unrestricted (every pre-existing blueprint)

// ActorProfile (host-resolved, same pattern as ConcurrencyScopeKey)
public IReadOnlySet<string> Capabilities { get; init; }
public bool HasCapability(IReadOnlyList<string>? requiredCapabilities); // any-of match
```

`RoleGates` already existed on `QueueDefinition` as a declared-but-completely-unused field before
this feature — reused here rather than adding a fourth "who can access this" concept alongside the
also-pre-existing (and, until now, unenforced — see below) `ServiceBlueprintRouteDefinition.RequiresRole`.

An **any-of** list: `["team-a", "team-b"]` means either team is eligible, since "other teams might
also be eligible for the same queue" is a real scenario, not a single-role gate. Enforced at one
choke point — `ProcessManagerEngine.HasQueueEligibility`, wired into both `CanViewQueue` (so an
ineligible actor's cursor is invisible everywhere: the worklist, `GetCurrent`, and `Advance`'s own
target resolution) and `CanStartInitialState` (so ineligibility also blocks starting fresh work in
a gated queue, not just acting in one).

**A profile with every allow-list empty is exempt from capability gating too** — this is what
keeps `ActorProfile.UnrestrictedOwner` (and every `GetCurrent`/`Advance` overload that defaults to
it) working unchanged once a real blueprint's queue starts declaring `RoleGates`, the same way
those calls were never restricted by queue name either.

## Claim/ownership — per-cursor, scoped to a cursor's dwell at its current node

```csharp
ServiceRequestResponseEnvelope ClaimWorkItem(string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);
ServiceRequestResponseEnvelope ReleaseWorkItem(string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);
QueueWorkItem? ClaimNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile);
```

Ownership lives on `RequestCursor.AssignedTo`/`AssignedAt` — per-cursor, not per-instance, matching
the engine's own multi-cursor model (a Split/Join instance can have simultaneous cursors in
different queues) and `GetQueueWorkItems`' own per-row granularity. A claimed cursor is **hidden
entirely** from every other actor — not shown as a disabled row — enforced at the same
`FindAccessibleWorkItems` choke point `Advance`'s own target resolution uses, so a teammate who
already has the `instanceId`/`cursorId` of a claimed item and calls `Advance` directly on it gets
`INVALID_TRANSITION`, the same as any other genuinely inaccessible item.

`QueueWorkItemClaimState` (`Unclaimed`/`ClaimedByMe`) is a field on `QueueWorkItem`, orthogonal to
`QueueWorkItemStatus` — status answers "what can be done", claim state answers "who's doing it".
Non-null only for a genuine shared-pool `Actionable` row on a non-owner-restricted queue; null for
`Waiting`/`Done` rows and owner-restricted queues, since there's nothing to claim there. There's no
"claimed by someone else" value — that row simply never appears for anyone but its claimant.

### Claim lifecycle — which transitions preserve it, which clear it

| Transition | Claim preserved? |
|---|---|
| Admin `"change:"` jump | Yes |
| Plain stage→stage hop (no gateway) | Yes |
| Split gateway crossing | **No** — cleared |
| Join gateway crossing/release | **No** — cleared |

Every Split/Join gateway-advance path mints a brand-new `RequestCursor` object (`new RequestCursor
{ ... }`), never a `with` update, so nothing carries `AssignedTo` forward even though the field
exists — no propagation code was written for this; its *absence* is the design, mirroring
Camunda's own per-task-instance assignee default. The two paths that preserve a cursor in place
(the admin jump, and a plain hop with no intervening gateway) both go through `MoveCursor`'s own
`c with { ... }`, which genuinely carries every field forward.

### The zero-cursor edge case

`CreateNewInstance` leaves `Cursors = []` until an instance crosses its first gateway — not a rare
edge case: `njf-contributions.json`'s own initial stage already sits in the shared "caseworker"
queue, so a freshly created, unclaimed bulk upload sits in exactly this state until someone claims
it. `RequestCursor.PrimaryCursorId` (`"$primary"`) is the well-known cursor id for claiming in this
state; `ClaimWorkItem` materializes a real cursor the first time it's used, the same shape the
engine already builds the first time such an instance crosses a real gateway. One non-obvious,
tested consequence: once materialized, the instance permanently switches onto the multi-cursor
`Advance` code path for the rest of its life, even for a blueprint with no gateways at all.

### No `expectedStateVersion` on claim/release — and why

Unlike `Advance`, which needs a caller-supplied expected version because it carries real,
user-typed field edits that must not silently overwrite a concurrent change, claiming carries no
field edits to lose. `ClaimWorkItem`/`ReleaseWorkItem` read fresh and retry their own internal
compare-and-swap (`IServiceRequestStore.TrySaveIfVersionMatches`) a bounded number of times, rather
than asking the caller to pre-supply a version it may not have freshly fetched.

### Atomicity

`IServiceRequestStore.TrySaveIfVersionMatches` is the toolkit's sole compare-and-swap primitive.
Its default interface implementation is a plain check-then-save — **not atomic**, provided only so
an existing host-authored `IServiceRequestStore` keeps compiling. The shipped
`InMemoryServiceRequestStore` overrides it with a genuine `ConcurrentDictionary`-backed CAS
(`TryAdd`/`TryUpdate`). Any store backing real concurrent claiming must provide a real atomic
implementation — a persistent store typically does this with a conditional update
(`UPDATE ... WHERE StateVersion = @expected`). `Advance()`'s own ordinary transition saves route
through this same primitive now too, closing the identical race for two caseworkers clicking
"approve" on the same unclaimed item simultaneously — not just explicit claims.

### `ClaimNextAvailableWorkItem` — deliberately simple in v1

For an automated/scaled-out caller: atomically claims the single oldest eligible, unclaimed,
Actionable row (by `CreatedAt`, tiebroken by `InstanceId` for determinism), retrying against the
next-oldest candidate if a concurrent caller wins the race on the first one. **No lease, no
heartbeat, no auto-expiry back to the pool.** A claim only clears via explicit `ReleaseWorkItem` or
a workflow transition — a worker that claims a row and then crashes mid-processing leaves it stuck
until someone else with the same eligibility releases or advances it. This is v1's known
limitation, not an oversight: the reference precedent (Camunda's `fetchAndLock` with a
`lockDuration` and `extendLock` heartbeat) is meaningfully more machinery, and nothing in the
concrete scenarios driving this feature needed it yet. A future version adding leasing would extend
`RequestCursor` with an expiry timestamp and a background sweep — a clean, additive extension, not
a redesign.

## Audit log

```csharp
public interface IAuditLogStore
{
    void Record(AuditEvent auditEvent);
    IReadOnlyList<AuditEvent> GetByInstance(string instanceId);
    IReadOnlyList<AuditEvent> Query(string? instanceId = null, string? actor = null,
        AuditEventSeverity? minimumSeverity = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        int pageIndex = 0, int pageSize = 50);
}
```

The same "engine defines interface, host implements storage" pattern as
`IServiceRequestFileStorage`/`IBulkDatasetStore`. `ProcessManagerEngine`'s constructor takes an
optional `IAuditLogStore`, defaulting to `InMemoryAuditLogStore` (process-lifetime) — audit always
works, even for a host that wires nothing.

`Advance` emits an `AuditEventType.Transition` event for every real transition (the `"change:"`
jump, a plain hop, a multi-cursor move, a Split fan-out, a Join arrival/release) — `Actor` is
whatever `userId` was passed to that call, including when it's a support-system poll/webhook
resolution recursing back through `Advance` (attributed to the instance's own owning user, the
same way every other call reaching that method already is — there is no separate "system"
sentinel). `ClaimWorkItem`/`ReleaseWorkItem` emit `Claimed`/`Released`. `AuditEventType.Reassigned`
is reserved for the future reassignment feature below, unused in v1.

**`Reset`/`ResetAll` deliberately do not purge the audit log** — a considered decision, not an
oversight: an audit trail outliving the record it describes is the point (Camunda's own History
tables survive process instance deletion), and silently wiping history on every demo reset would
be the wrong default. A host wanting log cleanup on reset calls its own store directly — nothing
in `IAuditLogStore`'s contract offers one.

## Not built yet — the seams that don't preclude them

**Reassignment** (a manager moving a claim from one person to another, e.g. someone's off sick):
`AssignedTo`/`AssignedAt` carry no type-level "only the holder can change this" invariant — only
`ReleaseWorkItem`'s own logic makes release self-service-only today. A future
`ReassignWorkItem(instanceId, cursorId, fromUserId, toUserId, reassignedBy)` slots in as a third
sibling method doing what `ClaimWorkItem`/`ReleaseWorkItem` already do, plus an authorization check
and the already-reserved `AuditEventType.Reassigned` event.

**Anonymous token hand-off** (a backstage process handing an instance to a not-yet-identified
anonymous citizen — a magic link or reference number): `ClaimInstances` already re-keys
`ServiceRequest.UserId` wholesale, but only for "the same browser session later signs in as
itself." A genuinely different operation — handing an instance to someone else entirely — plugs in
as a sibling method (e.g. `RedeemAccessGrant(token) -> instanceId`), not a change to
`ClaimInstances`. It composes cleanly: once resolved to a `userId`, the granted person
authenticates through the same `Advance`/`ClaimWorkItem` surface as anyone else — `RequestCursor.AssignedTo`
doesn't care how a `userId` came to exist.

## `RequiresRole` — now genuinely enforced

`ServiceBlueprintRouteDefinition.RequiresRole` existed on routes before this feature, but was never
actually checked against the accessing actor — `BuildAvailableActions` only ever stripped a
role-gated route when there was *no queue context at all*, never validating the specific role in
the normal case. It's now checked for real, against the same `ActorProfile.Capabilities` set
`RoleGates` checks — a route declaring `requiresRole: "senior-caseworker"` is excluded from
`AvailableActions` (not merely disabled) unless the accessing actor's `Capabilities` contains that
value. Reuses `Capabilities` rather than inventing a near-duplicate `Roles` set, since both already
express "does this actor hold X".

## Worked example — the reference app

Both `juggling-licence.json` and `njf-contributions.json` independently declare a queue literally
named `"caseworker"` — before `RoleGates` existed, Casey (juggling-licence) and Priya (NJF
operations) could each already see the *other's* blueprint's rows purely because the queue keys
collided, regardless of which blueprint either of them actually works on. Fixed and demonstrated
together:

- `njf-contributions.json`'s `"caseworker"` queue: `"roleGates": ["njf-contributions-review"]`.
- `juggling-licence.json`'s `"caseworker"` queue: `"roleGates": ["juggling-licence-review"]`.
- `ReferenceActors.CaseworkerProfile()` (Casey): `Capabilities = ["juggling-licence-review"]`.
- `ReferenceActors.NjfOperationsProfile()` (Priya): `Capabilities = ["njf-contributions-review"]`.
- `ReferenceActors.ProfileForCaseworkerUser(userId)` resolves the right one per signed-in demo
  user — every generic backstage route (the worklist, an item's own page, advance, claim/release)
  is shared across both personas, so it can't hardcode either profile without locking one out of
  the other's now-gated queue.

Claim/Release buttons are wired into `/caseworker/queue`'s own worklist template, rendered as
"Pick up"/"Put back" — plain-English labels for what this doc and the engine API still call
claim/release internally (see `WorklistExtensions.RenderClaimReleaseControl`'s own doc comment for
why the rendered copy doesn't have to match the engine's internal verb). A claimed row shows a
"With you" tag plus a Put back button; an unclaimed shared-pool row shows a Pick up button.
