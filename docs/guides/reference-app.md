# The Wayfinder Reference App

What you get out of the box when you wire Wayfinder into a host, and exactly how little of it
is actually Wayfinder's problem to solve.

`Wayfinder.AppHost` + `Wayfinder.ReferenceApp` (with `Wayfinder.ServiceDefaults` for the usual
Aspire plumbing) is a small ASP.NET Core app in this repo that demonstrates every package —
`Wayfinder`, `Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`,
`Wayfinder.Editor` — running together, with real GOV.UK Design System rendering and a real
(if intentionally minimal) auth boundary. It is **completely transient**: seeded from a JSON
file on disk, everything else in memory, nothing survives a restart. That's deliberate — this
host exists to be booted and reset constantly by Playwright (`DELETE /api/test/reset`), not to
be a real content store. See ["Service blueprint definitions: seed vs. save"](#service-blueprint-definitions-seed-vs-save)
below for exactly what that means and how to prove it to yourself.

**Run it:** `dotnet run --project Wayfinder.AppHost`, or the "C#: Aspire (Full Stack)" launch
config in VS Code (`.vscode/launch.json`) — the Aspire dashboard opens automatically with a
link straight into the app. Sign in as `applicant@example.test` or `caseworker@example.test`
(password `wayfinder-demo`) at `/account/login`.

## The demo service

Seeded with **"Apply for a licence to hold a juggling event"** — GOV.UK Service Manual's own
long-running teaching exemplar, chosen because it's a small, low-stakes fictional service
everyone already recognises rather than something bespoke this repo would need to explain from
scratch. It models NN/g's frontstage/backstage split
(https://www.nngroup.com/articles/service-blueprints-definition/) across two queues:

- **`citizen`** (frontstage) — the applicant's own journey at `/apply`: enter details, describe
  the event, check answers and declare, then wait behind the line of visibility.
- **`caseworker`** (backstage) — the review team's worklist at `/caseworker/queue`: see what's
  waiting, approve or reject.

A third **support-systems** lane (a downstream/API-driven actor — the third leg of NN/g's model)
is a deliberate, explicitly-noted gap, not built yet.

Every stage route targets a real gateway (`ServiceBlueprint.ValidateGatewayRouting()`'s rule) —
even the trivial single-route handoffs get their own pass-through gateway. See
[reference-service-blueprint-contract.md](./reference-service-blueprint-contract.md) for why
that rule exists.

## Every extension point, wired up

Wayfinder's own packages define the interfaces; a host supplies the implementations. Here's
what this reference app plugs in for each one, and — where a real, more capable implementation
already exists — what [`Wayfinder.Umbraco`](https://github.com/jonnymuir/Wayfinder.Umbraco)
does instead. (Checked directly against `Wayfinder.Umbraco`'s current source, not assumed —
see the file paths below.)

| Extension point | This reference app | Wayfinder.Umbraco |
|---|---|---|
| `IServiceBlueprintStore` (boot-time definition load) | `FilesystemServiceBlueprintStore` reading `service-blueprints/*.json` — a type `Wayfinder.Engine` already ships, not custom code | `UmbracoServiceBlueprintBootStore` — reads the `wayfinderServiceBlueprint` DB table at boot |
| `IServiceBlueprintSourceStore` (authoring saves — editor/REST/MCP) | `InMemoryRuntimeServiceBlueprintSourceStore` — a `Dictionary`, lost on restart | `UmbracoServiceBlueprintStore` — same DB table, atomic optimistic-concurrency `UPDATE ... WHERE Version = @expected`, **plus** `ServiceBlueprintHandler` (a real uSync `SyncHandlerRoot`) giving every definition export/import portability across environments, the same as any other uSync-tracked Umbraco content |
| `IServiceRequestStore` (running instance state) | Nothing registered — `ProcessManagerEngine`'s own default, `InMemoryServiceRequestStore`, applies | `UmbracoServiceRequestStore` — the `wayfinderServiceRequest` DB table, durable across an app-pool recycle; sliding expiry for an anonymous visitor, permanent for a signed-in member |
| `IQueueCapabilitiesProvider` | `StaticQueueCapabilitiesProvider` declaring exactly what `citizen`/`caseworker` render (`Services/ReferenceActors.cs`) | Same pattern — declares whatever a host's own rendering surface actually supports |
| `IServiceContentSanitizer` | `PassthroughSanitizer` — seed content is developer-authored, no XSS risk | `ServiceContentSanitizer` — a real Ganss.Xss GDS allowlist, because real content is backoffice/user-authored |
| Auth | A hand-rolled in-memory cookie login, two fixed demo users (`Services/DemoUsers.cs`) — deliberately not OIDC, see below | `WayfinderUmbracoAuthorizationPolicies` — two named policies a host binds to its own scheme: `ServiceRequestPolling` (citizen-facing, host wires it — e.g. to its member cookie) and `BlueprintsAdmin` (backoffice authoring, self-registered against Umbraco backoffice group membership) |
| Stage rendering | `ComponentHtmlRenderer.cs` — hand-rolled server-side HTML using the real `govuk-frontend` package (see below) | Ships its own Razor views/tag helpers — a complete GDS-style rendering layer, not something a host writes itself |
| Wiring it all together | Six separate `builder.Services.AddSingleton(...)` calls in `Program.cs` (deliberately visible — see "why so much wiring?" below) | One call: `services.AddWayfinderUmbraco()` |

Why is Wayfinder.Umbraco's own auth split into two named policies rather than one, and why
does it only self-register one of them? `BlueprintsAdmin` (backoffice authoring) is something
Wayfinder.Umbraco already has full information to decide — Umbraco's own
`IBackOfficeSecurityAccessor` — so it wires that policy itself. `ServiceRequestPolling`
(citizen-facing) depends entirely on how a specific host authenticates its own members (Prism's
`PrismMemberCookie`, someone else's OIDC scheme, whatever), so Wayfinder.Umbraco carries no
opinion about it and leaves the policy for the host to register. This reference app's own
two-user cookie login demonstrates both lanes at once with one mechanism, because it doesn't
need to prove a real member-identity integration.

### Why is `Program.cs` so long, if the point is "easy to implement"?

It isn't, particularly — but it *looks* long because every extension point is registered
explicitly and commented, on purpose, so this app doubles as a worked example. A real host
using `Wayfinder.Umbraco` collapses the entire "Wayfinder wiring" block above into
`services.AddWayfinderUmbraco()`. The rest of `Program.cs` — routes, the hand-rolled HTML
renderer, the cookie login — exists because this reference app doesn't have a rendering
package or an identity model to lean on the way an Umbraco host does; it's demonstrating the
*shape* of that layer, not claiming Wayfinder needs you to write it from scratch.

## Service blueprint definitions: seed vs. save

This is the one distinction worth being precise about, because it's easy to assume "the editor
saved it" means "it's on disk now."

**At boot**, `FilesystemServiceBlueprintStore.LoadDefinitions()` reads every `*.json` file in
`service-blueprints/` once, keyed by each file's own `definitionKey` (not the filename):

```csharp
builder.Services.AddSingleton<IServiceBlueprintStore>(
    _ => new FilesystemServiceBlueprintStore(Path.Combine(builder.Environment.ContentRootPath, "service-blueprints")));
```

That's the entire seed mechanism — no custom code, just the store `Wayfinder.Engine` already
ships. `service-blueprints/juggling-licence.json` is real output from this app's own authoring
API (`GET /wayfinder/service-blueprint-authoring/blueprints/juggling-licence`), captured once
and committed — not hand-written.

**A save** — from `/service-blueprint-editor`, the REST API, or MCP — goes through
`InMemoryRuntimeServiceBlueprintSourceStore.SaveAsync`, which keeps the new version in a
private `Dictionary` and calls `IProcessManager.UpdateDefinition(...)` so the change is live
for the very next request. It never touches disk.

Proven, not just asserted — this is exactly reproducible against a running instance:

```bash
# 1. Note the seed file's checksum
md5 Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json

# 2. Change something and save it through the real authoring API
curl -X PUT http://localhost:<port>/wayfinder/service-blueprint-authoring/blueprints/juggling-licence \
  -H "Content-Type: application/json" --data @modified.json
# -> {"status":"Saved","newVersion":2,...}

# 3. The file on disk is byte-identical — unchanged
md5 Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json

# 4. But the running app now serves the change immediately
curl http://localhost:<port>/wayfinder/service-blueprint-authoring/blueprints/juggling-licence
# -> displayName reflects the edit, version: 2

# 5. Restart the process — it reverts to exactly what's on disk
#    (version: 1, the original displayName)
```

That's the whole point of `InMemoryRuntimeServiceBlueprintSourceStore`'s design: a Playwright
run (or anyone poking the editor locally) can save, break things, and reset
(`DELETE /api/test/reset`, Development-only) without ever mutating a file that's checked into
git.

**A real host persists saves.** `Wayfinder.Umbraco`'s `UmbracoServiceBlueprintStore` writes to
the same `wayfinderServiceBlueprint` database table `UmbracoServiceBlueprintBootStore` reads at
boot (created by the `CreateServiceBlueprintTable` migration), with a genuine atomic
compare-and-swap:

```csharp
// Wayfinder.Umbraco/Services/UmbracoServiceBlueprintStore.cs
var rowsAffected = db.Execute(
    "UPDATE wayfinderServiceBlueprint SET DisplayName = @0, Json = @1, Version = @2, UpdatedUtc = @3 " +
    "WHERE DefinitionKey = @4 AND Version = @5",
    blueprint.DisplayName, json, newVersion, DateTime.UtcNow, blueprint.DefinitionKey, expectedVersion);
```

On top of that, `Wayfinder.Umbraco/SyncHandlers/ServiceBlueprintHandler.cs` registers a real
uSync `SyncHandlerRoot` for these DB-backed definitions — the same export/import portability
any other uSync-tracked Umbraco content gets. A blueprint authored in one environment's
backoffice can be exported to disk in uSync's own format, committed, and imported into another
environment (staging → production, or teammate → teammate) exactly like a doc type or content
node would be. `Wayfinder` itself has no opinion about this at all — it's a capability
`Wayfinder.Umbraco` chose to add on top, specific to hosts that already use uSync.

## Where the code lives

- `Wayfinder.AppHost/Program.cs` — the Aspire orchestrator (one resource, no containers)
- `Wayfinder.ReferenceApp/Program.cs` — all the wiring described above, plus the routes
- `Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json` — the seed
- `Wayfinder.ReferenceApp/Services/` — every custom implementation in the table above
- `Wayfinder.ReferenceApp/wwwroot/` — the real vendored `govuk-frontend` CSS/JS/fonts/images
- `Wayfinder.ReferenceApp.Tests/` — the Playwright suite (auth, the full citizen→caseworker→citizen
  handoff, the editor/authoring wiring) — run single-worker, since the backend is one shared
  in-memory process with fixed demo identities, not per-test isolated
