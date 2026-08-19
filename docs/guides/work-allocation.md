# Work allocation: queue eligibility, pickup/ownership, and audit

How Wayfinder models real work-allocation scenarios beyond a static "can this actor see this
queue" check: which team is eligible to work a queue, who currently owns one specific item within
it, an atomic "pick up next available" primitive for automated/scaled-out workers, and a full audit
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

- **Queue eligibility** — *which team* is allowed to see/pick up from this queue at all.
- **Pickup/ownership** — *which specific person, right now* owns one particular item within a
  queue this actor is already eligible for.

Both apply to any shared (non-owner-restricted) queue, independently of each other and of whether
the other is even in use. Queue eligibility is opt-in (a queue with no declared `RoleGates` stays
fully unrestricted, exactly as it always has); pickup/ownership is **not** — see the mandatory-pickup
rule immediately below.

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

## Pickup/ownership — per-cursor, scoped to a cursor's dwell at its current node

```csharp
ServiceRequestResponseEnvelope PickupWorkItem(string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);
ServiceRequestResponseEnvelope PutbackWorkItem(string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);
QueueWorkItem? PickupNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile);
```

### The rule: if a row isn't assigned to you, you can't action it — full stop

Pickup is mandatory on every shared (non-owner-restricted) queue, with **no per-queue opt-out**.
This applies whether or not the queue declares a `QueueDefinition.AssignmentPolicy` at all:

- A queue declaring `"team-tray"` scopes *who* may pick a row up to a specific team's members —
  see `docs/guides/team-assignment.md`.
- A queue declaring `"assign-to-initiator"` has nothing to pick up — it's owned by whoever started
  it the instant it exists.
- A queue declaring **no** `AssignmentPolicy` at all still requires pickup — it's just not scoped
  to any particular team, so any actor already eligible to see the queue may pick a row up. This is
  the same mandatory-pickup rule as `"team-tray"`, minus the team-membership restriction — not a
  legacy, optional-pickup mode. Every real blueprint in this reference app either declares
  `"team-tray"`/`"assign-to-initiator"` explicitly, or falls into this "no policy, still mandatory"
  bucket — there is no third, opt-out shape.

The single enforcement choke point is `ProcessManagerEngine.IsEntitledToActNow`: a row's
`AvailableActions` stays empty — regardless of how many `EligibleActions` the row has — unless
`RequestCursor.AssignedTo`/the team's `QueueAssignment` already matches the calling `userId`.
`Advance()` re-derives this fresh on every call from the current, persisted state, so a client
that bypasses whatever the UI renders and posts a trigger directly for a not-picked-up row gets
`INVALID_TRANSITION`, the same as any other genuinely inaccessible action — there's no
client-trust shortcut to defeat.

**The one genuine exemption**: `ActorProfile.RestrictToInstanceOwner` — an owner-restricted
(citizen-style) profile's own instance has exactly one possible actor by construction, so
"assignment" isn't a concept that applies there at all. This is *not* a second opt-out for shared
queues; it only ever applies to a profile that is itself restricted to viewing its own instances
(a citizen journey, or the synthetic `ActorProfile.UnrestrictedOwner` a support-system
poll/webhook resolution recurses through — see `ResolveSupportSystemOutcome`, which always
advances via `UnrestrictedOwner` using the instance's own owning `userId`, precisely so an
automation queue's system-driven resolution never needs a human-style pickup of its own).

**Terminology**: "pick up"/"put back" — not "claim"/"release", "unclaimed"/"claimed", or
"assign"/"unassign" — is the vocabulary used throughout the engine API, the worklist UI, the audit
log, and this document.
Deliberately not "claim": that word already means something else entirely in the same codebase
(OAuth/identity claims — a token's own claims, nothing to do with queue ownership), and reusing it
for a second, unrelated concept invites exactly the kind of confusion ubiquitous language is meant
to prevent. Not "assign"/"unassign" either, even though that's the closer real-world
UK-government-service convention (e.g. MyHMCTS): "pick up"/"put back" reads as plain English and
pairs as an obvious verb/its-opposite in a way "assign"/"unassign" doesn't as cleanly. No NN/g
guidance was found for this specific term (checked) — this is a design-school judgment call, not
one backed by a specific NN/g article.

Ownership lives on `RequestCursor.AssignedTo`/`AssignedAt` — per-cursor, not per-instance, matching
the engine's own multi-cursor model (a Split/Join instance can have simultaneous cursors in
different queues) and `GetQueueWorkItems`' own per-row granularity. A picked-up cursor is **hidden
entirely** from every other actor — not shown as a disabled row — enforced at the same
`FindAccessibleWorkItems` choke point `Advance`'s own target resolution uses, so a teammate who
already has the `instanceId`/`cursorId` of a picked-up item and calls `Advance` directly on it gets
`INVALID_TRANSITION`, the same as any other genuinely inaccessible item.

`QueueWorkItemPickupState` (`NotPickedUp`/`PickedUpByMe`) is a field on `QueueWorkItem`, orthogonal
to `QueueWorkItemStatus` — status answers "what can be done", pickup state answers "who's doing
it". Non-null for a genuine shared-pool row on a non-owner-restricted queue in either
`QueueWorkItemStatus.Unassigned` (not yet picked up) or `Actionable` (already picked up, by the
caller) state — whether or not the queue declares an `AssignmentPolicy`, since pickup is mandatory
either way. Null for `Waiting`/`Done` rows, owner-restricted queues, and `"assign-to-initiator"`
queues, since none of those have anything to pick up. There's no "picked up by someone else" value
— that row simply never appears for anyone but whoever holds it.

### Pickup lifecycle — which transitions preserve it, which clear it

| Transition | Pickup preserved? |
|---|---|
| Admin `"change:"` jump | Yes |
| Plain stage→stage hop (no gateway) | Yes |
| Split gateway crossing | **No** — cleared |
| Join gateway crossing/release | **No** — cleared |

Every Split/Join gateway-advance path mints a brand-new `RequestCursor` object (`new RequestCursor
{ ... }`), never a `with` update, so nothing carries `AssignedTo` forward even though the field
exists — no propagation code was written for this; its *absence* is the design: a cursor that
crosses a gateway is, structurally, a new unit of work in a new queue, and inheriting the old
cursor's pickup into that new context isn't the right default. The two paths that preserve a
cursor in place (the admin jump, and a plain hop with no intervening gateway) both go through
`MoveCursor`'s own `c with { ... }`, which genuinely carries every field forward.

### The zero-cursor edge case

`CreateNewInstance` leaves `Cursors = []` until an instance crosses its first gateway — not a rare
edge case: `njf-contributions.json`'s own initial stage already sits in the shared "caseworker"
queue, so a freshly created, not-picked-up bulk upload sits in exactly this state until someone
picks it up. `RequestCursor.PrimaryCursorId` (`"$primary"`) is the well-known cursor id for picking
up in this state; `PickupWorkItem` materializes a real cursor the first time it's used, the same
shape the engine already builds the first time such an instance crosses a real gateway. One
non-obvious, tested consequence: once materialized, the instance permanently switches onto the
multi-cursor `Advance` code path for the rest of its life, even for a blueprint with no gateways at
all.

### No `expectedStateVersion` on pickup/putback — and why

Unlike `Advance`, which needs a caller-supplied expected version because it carries real,
user-typed field edits that must not silently overwrite a concurrent change, picking up carries no
field edits to lose. `PickupWorkItem`/`PutbackWorkItem` read fresh and retry their own internal
compare-and-swap (`IServiceRequestStore.TrySaveIfVersionMatches`) a bounded number of times, rather
than asking the caller to pre-supply a version it may not have freshly fetched.

### Atomicity

`IServiceRequestStore.TrySaveIfVersionMatches` is the toolkit's sole compare-and-swap primitive.
Its default interface implementation is a plain check-then-save — **not atomic**, provided only so
an existing host-authored `IServiceRequestStore` keeps compiling. The shipped
`InMemoryServiceRequestStore` overrides it with a genuine `ConcurrentDictionary`-backed CAS
(`TryAdd`/`TryUpdate`). Any store backing real concurrent pickup must provide a real atomic
implementation — a persistent store typically does this with a conditional update
(`UPDATE ... WHERE StateVersion = @expected`). `Advance()`'s own ordinary transition saves route
through this same primitive now too, closing the identical race for two caseworkers clicking
"approve" on the same not-picked-up item simultaneously — not just explicit pickups.

### `PickupNextAvailableWorkItem` — deliberately simple in v1

For an automated/scaled-out caller: atomically picks up the single oldest eligible, not-picked-up,
Actionable row (by `CreatedAt`, tiebroken by `InstanceId` for determinism), retrying against the
next-oldest candidate if a concurrent caller wins the race on the first one. **No lease, no
heartbeat, no auto-expiry back to the pool.** A pickup only clears via explicit `PutbackWorkItem` or
a workflow transition — a worker that picks up a row and then crashes mid-processing leaves it
stuck until someone else with the same eligibility puts it back or advances it. This is v1's known
limitation, not an oversight: a leasing scheme (an expiry timestamp plus a heartbeat to extend it)
is meaningfully more machinery, and nothing in the concrete scenarios driving this feature needed
it yet. A future version adding leasing would extend `RequestCursor` with an expiry timestamp and a
background sweep — a clean, additive extension, not a redesign.

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
sentinel). `PickupWorkItem`/`PutbackWorkItem` emit `PickedUp`/`PutBack`. `AuditEventType.Reassigned`
is reserved for the future reassignment feature below, unused in v1.

**`Reset`/`ResetAll` deliberately do not purge the audit log** — a considered decision, not an
oversight: an audit trail outliving the record it describes is the point, and silently wiping
history on every demo reset would be the wrong default. A host wanting log cleanup on reset calls
its own store directly — nothing in `IAuditLogStore`'s contract offers one.

## Not built yet — the seams that don't preclude them

**Reassignment** (a manager moving a pickup from one person to another, e.g. someone's off sick):
`AssignedTo`/`AssignedAt` carry no type-level "only the holder can change this" invariant — only
`PutbackWorkItem`'s own logic makes putting back self-service-only today. A future
`ReassignWorkItem(instanceId, cursorId, fromUserId, toUserId, reassignedBy)` slots in as a third
sibling method doing what `PickupWorkItem`/`PutbackWorkItem` already do, plus an authorization check
and the already-reserved `AuditEventType.Reassigned` event.

**Anonymous token hand-off** (a backstage process handing an instance to a not-yet-identified
anonymous citizen — a magic link or reference number): `ClaimInstances` already re-keys
`ServiceRequest.UserId` wholesale, but only for "the same browser session later signs in as
itself." (Its own name predates this feature's pick-up/put-back vocabulary and refers to a
genuinely different operation — re-keying an instance's owning identity, not queue ownership —
so it keeps its own name rather than being folded into this one.) A genuinely different
operation — handing an instance to someone else entirely — plugs in as a sibling method (e.g.
`RedeemAccessGrant(token) -> instanceId`), not a change to `ClaimInstances`. It composes cleanly:
once resolved to a `userId`, the granted person authenticates through the same
`Advance`/`PickupWorkItem` surface as anyone else — `RequestCursor.AssignedTo` doesn't care how a
`userId` came to exist.

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
  user — every generic backstage route (the worklist, an item's own page, advance, pickup/putback)
  is shared across both personas, so it can't hardcode either profile without locking one out of
  the other's now-gated queue.

Pickup/putback buttons are wired into `/caseworker/queue`'s own worklist template, rendered as
"Pick up"/"Put back" — the same vocabulary as the engine API itself
(`WorklistExtensions.RenderPickupPutbackControl`). A picked-up row shows a "With you" tag plus a
Put back button; a not-picked-up shared-pool row shows a Pick up button.
