# Queue worklist filtering, sorting, and search

How `ProcessManagerEngine.GetQueueWorkItems` lets a host build a real caseworker worklist,
status filtering (actionable / waiting / done), sorting, free-text search, and pagination, rather
than the fixed, unfilterable list it started as. For whoever is *building* a Wayfinder host; a
blueprint author never needs any of this.

This document is also exposed as an MCP resource
(`service-blueprint-docs://queue-worklist-filtering`) so an agent can fetch it directly without
repo access.

---

## The problem this solves

`GetQueueWorkItems` used to hard-exclude any instance with no available actions and no join-gateway
wait, a genuinely completed (terminal) instance simply vanished from the worklist forever,
reachable only by a remembered URL. That was fine as long as nothing needed to look back at
finished work, but a real caseworker worklist does: "show me what I've already dealt with", "hide
the ones I'm just waiting on", "find the application for a specific applicant".

## `QueueWorkItemStatus`, three buckets, not a ladder

```csharp
public enum QueueWorkItemStatus { Actionable, Waiting, Done }
```

Each row is classified independently, a genuine multi-select, not a linear "show more" scale,
since "include Done" and "exclude Waiting" are independent asks:

- **`Actionable`**, has at least one available action. Today's plain, undecorated row.
- **`Waiting`**, the actor's own cursor is parked at a join gateway, waiting on another queue
  (another team, or an automation queue waiting on a support system, see
  [support-systems.md](./support-systems.md)). `AvailableActions` is always empty for these.
  `QueueWorkItem.IsWaiting` is a derived convenience (`Status == Waiting`), not an independent
  field.
- **`Done`**, genuinely resolved: no outbound routes, rendered as a confirmation panel. This
  reuses the same "is this actually terminal" check `GetCurrentOrStartFresh` uses (see
  [request-concurrency.md](./request-concurrency.md)), **not** simply "has no available actions".
  A row can have zero actions for reasons that aren't completion: the actor lacks permission to
  act in that queue, or every outgoing route is `showWhen`-hidden. Neither of those is `Done`, and
  neither is `Waiting` or `Actionable` either, such a row has no status at all and stays
  invisible under every filter combination, exactly as it always has. This is deliberate, not an
  oversight: nothing asked for a fourth bucket, and inventing one risks mislabeling a row that's
  actually still live as "finished".

## `GetQueueWorkItems`, the query surface

```csharp
QueueWorkListEnvelope GetQueueWorkItems(
    ActorProfile accessProfile,
    IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
    QueueWorkListSort sort = QueueWorkListSort.Default,
    string? searchText = null,
    int pageIndex = 0,
    int pageSize = 20);
```

**`statuses`: `null` vs. `[]` is a real, load-bearing distinction.** `null` (the C# default) means
"apply the engine's own default view", `{Actionable, Waiting}`, reproducing every existing
caller's behaviour unchanged. An explicit, non-null empty collection means "show nothing",
respected literally. A plain HTML checkbox form can't distinguish "nothing submitted yet" from
"the user unchecked every box and submitted", both produce zero `status` values on the wire, so
a route binding this from a query string needs its own way to tell the two apart (the reference
app's own `/caseworker/queue` route uses a hidden `statusFilterApplied` field; see its source for
the exact pattern).

**`sort`**, `QueueWorkListSort.Default` reproduces the worklist's original fixed order (blueprint
display name, then stage display name, then instance id). Every other value
(`CreatedAtNewestFirst`/`OldestFirst`, `UpdatedAtNewestFirst`/`OldestFirst`) still ends in an
instance-id tiebreak internally, `IServiceRequestStore.GetAll()` gives no ordering guarantee of
its own, so without a deterministic tiebreak a row could drift between pages across two requests
even with an otherwise-identical sort key.

**`searchText`**, case-insensitive substring match against the instance id, blueprint display
name, and stage display name, plus every raw value in the instance's `FieldValues` (stringified;
a `System.Text.Json.JsonElement` value is real here, not merely a defensive case, so it's matched
against its own JSON text rather than its CLR type name). Evaluated with a full in-memory scan,
no index, fine at this toolkit's actual scale, since `IServiceRequestStore` has no query pushdown
of its own today for anything to build an index against. A persistent store implementation wanting
one is a real future extension point, not something built speculatively here.

One known, accepted quirk: a `FieldValues` value that's a JSON object or array stringifies to its
raw JSON text, so a search could occasionally match an internal key name or a file's storage
metadata rather than genuinely relevant content, usually harmless (matching a filename fragment
is often exactly what's wanted), occasionally surprising. Not solved here; know it's possible.

**`pageIndex`/`pageSize`**, `pageIndex` clamped to `>= 0`, `pageSize` clamped to `[1, 100]`.
`QueueWorkListEnvelope.TotalMatchingCount` reflects the full match count independent of paging, for
rendering "page 2 of N" or a "showing X of Y" count.

## Extending row classification or search

Both `ClassifyStatus` and the search matcher live inside `ProcessManagerEngine` as private
per-item logic evaluated during `GetQueueWorkItems`'s own scan, there's no separate extension
point for either today, on the same "add one when a concrete need drives it" principle the rest of
this toolkit follows. If a host needs its own notion of "done" or its own searchable field beyond
raw `FieldValues`, that's a genuine gap to raise, not something to work around by post-processing
`QueueWorkListEnvelope.Items`, the status/search logic needs the underlying `ServiceRequest` and
`AccessibleWorkItem`, which the envelope's projection deliberately doesn't expose.
