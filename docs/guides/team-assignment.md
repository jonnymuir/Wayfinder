# Team-based work assignment: `AssignmentPolicy`, team trays, and initiator ownership

How Wayfinder scopes *who* may pick up a row on a shared queue to a specific team, or skips
pickup entirely because a row is already owned the instant it exists. This sits on top of the
mandatory-pickup rule described in `docs/guides/work-allocation.md`, read that first: pickup
itself is never optional on a shared queue, whether or not the queue declares anything here.

This document is also exposed as an MCP resource (`service-blueprint-docs://team-assignment`) so
an agent can fetch it directly without repo access.

---

## `QueueDefinition.AssignmentPolicy`

```csharp
public string? AssignmentPolicy { get; init; } // null | "assign-to-initiator" | "team-tray"
public string? OwningTeamId { get; init; }      // required for "team-tray"; optional otherwise
```

Three shapes, all mandatory-pickup (see `docs/guides/work-allocation.md`'s own rule), differing
only in *who* may act and whether there's a pickup step at all:

| `AssignmentPolicy` | Owner | Pickup? | Visible to |
|---|---|---|---|
| `null` (undeclared) | Whoever picks it up | Yes | Any actor eligible for the queue |
| `"team-tray"` | Whoever on the team picks it up | Yes, team members only | Team members (unpicked); holder only (picked up) |
| `"assign-to-initiator"` | Whoever started the instance | No, already owned | Owner only |

## `"assign-to-initiator"`: owned the instant it exists

Ownership is `ServiceRequest.UserId`, whoever's `Advance`/`GetCurrent` call actually created the
instance. `PickupWorkItem`/`PutbackWorkItem` both return `PICKUP_NOT_AVAILABLE` for these rows:
there's nothing to pick up or put back, by design (see `docs/guides/work-allocation.md`'s
"Not built yet, Reassignment" section for how a future manager-driven reassignment would slot in
without changing this).

`QueueWorkItemPickupState` is `null` for these rows (see `work-allocation.md`), the same "nothing
to pick up here" signal a `Waiting`/`Done` row gives, for a different reason.

## `"team-tray"`: visible to the team, actionable only once picked up

An unpicked team-tray row is visible to every member of `OwningTeamId` (via
`ActorProfile.TeamIds`/`IsTeamMember`), and to nobody outside it, a genuinely different gate from
plain queue eligibility (`QueueDefinition.RoleGates`/`ActorProfile.Capabilities`): an actor can be
fully eligible to view/act in the queue in general and still not be a member of the specific team
that owns this row. Once picked up, the row disappears from every other team member's view, the
same "hidden entirely, not shown-but-disabled" behavior any picked-up row gets (see
`work-allocation.md`).

- Pickup by a non-team-member → `TEAM_MEMBERSHIP_REQUIRED`.
- A second pickup attempt by a different team member once already held → `ALREADY_PICKED_UP`
  (the same error a no-policy queue gives, team membership only gates *who may attempt* pickup,
  not the outcome once something's already held).
- Putback is self-service only, same as every other queue (`work-allocation.md`'s own rule),
  returns the row to the tray, visible to every team member again as `Unassigned`.

## Where ownership actually lives: two different places, by design

Unlike a no-policy queue, where ownership lives directly on `RequestCursor.AssignedTo` (see
`work-allocation.md`), a queue declaring *any* `AssignmentPolicy` tracks ownership in
`ServiceRequest.QueueAssignments` (`Dictionary<queueKey, QueueAssignment>`) instead,
`RequestCursor.AssignedTo` is never touched for these. This split exists because a Split/Join
instance can revisit the *same* team-owned queue key across multiple cursor mints (a resubmit loop
through automation and back into the same "ops-team" queue, for instance), tying ownership to one
particular `RequestCursor` object would lose it the moment that cursor is replaced by a gateway
crossing, even though the *queue* itself hasn't changed. `QueueAssignments` is keyed by queue, not
by cursor, so it survives exactly that round trip (see
`TeamAssignmentTests.AssignToInitiator_SurvivesTheResubmitRoundTripThroughAutomationAndBackToTheSameQueue`).

`EstablishQueueAssignmentsIfNeeded` lazily seeds a `QueueAssignment` the first time any acting
user's call touches a policy-declaring queue on an instance, pre-filled with
`AssignedUserId = actingUserId` for `"assign-to-initiator"`, left unassigned (tray) for
`"team-tray"`. Deliberately centralized in one place rather than threaded into every individual
cursor-minting call site (Split fan-out, Join arrival/release), so a future new mint site can't
silently skip establishment.

## The queue-boundary reset

Crossing from one queue into a genuinely different one, even mid-instance, even from an
`"assign-to-initiator"` queue you personally own, never carries your ownership forward. The new
queue's own policy applies from scratch: a `"team-tray"` queue you land in starts `Unassigned`
regardless of who owned the row on the queue you just left (see
`TeamAssignmentTests.CrossingIntoAGenuinelyDifferentTeamOwnedQueue_StartsFreshUnderThatQueuesOwnPolicy`).
This mirrors the same "a cursor crossing a gateway is a new unit of work in a new queue" principle
`work-allocation.md`'s own pickup-lifecycle table already establishes for plain pickup.

## Worked example: the reference app

`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`'s `"caseworker"` queue declares
`assignmentPolicy: "team-tray"`, `owningTeamId` matching `DemoTeams.JugglingLicenceReviewers`,
`ReferenceActors.CaseworkerProfile()` (Casey, Jordan) carries that team id in
`ActorProfile.TeamIds`. `njf-contributions.json`'s own queue (`ReferenceActors.NjfTeamQueue`)
declares `assignmentPolicy: "assign-to-initiator"`, whoever from NJF operations (Priya, Sam)
uploads a file owns the resulting instance outright, with `ActorProfile.ConcurrencyScopeKey`
additionally treating every NJF operations user as one owner for concurrency purposes (see
`docs/guides/request-concurrency.md`) even though they can already all see/act on the same shared
queue.
