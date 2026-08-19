# The Wayfinder Reference App

What you get out of the box when you wire Wayfinder into a host, and exactly how little of it
is actually Wayfinder's problem to solve.

`Wayfinder.AppHost` + `Wayfinder.ReferenceApp` (with `Wayfinder.ServiceDefaults` for the usual
Aspire plumbing) is a small ASP.NET Core app in this repo that demonstrates every package —
`Wayfinder`, `Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`,
`Wayfinder.Rendering.GovUk`, `Wayfinder.Editor` — running together, with real GOV.UK Design
System rendering and a real (if intentionally minimal) auth boundary. See
[Package Architecture](#package-architecture) below for what each of these owns and why. It is
**completely transient**: seeded from a JSON
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
  the event, optionally upload a risk assessment, check answers and declare, then wait behind
  the line of visibility.
- **`caseworker`** (backstage) — the review team's worklist at `/caseworker/queue`: see what's
  waiting, approve or reject. The worklist's own status filter/sort/free-text search is covered
  separately — see [docs/guides/queue-worklist-filtering.md](./queue-worklist-filtering.md).
  Queue eligibility (which team can see a queue at all) and per-item pickup/ownership (Pick up/Put
  back buttons on the worklist) are covered in [docs/guides/work-allocation.md](./work-allocation.md);
  scoping pickup to a specific team, or skipping it because a row is owned the instant it's
  created, is covered in [docs/guides/team-assignment.md](./team-assignment.md).

A third **support-systems** lane (a downstream/API-driven actor — the third leg of NN/g's model)
now also runs alongside these two: **SafetyNet Underwriting**, a genuinely separate ASP.NET Core
app (`SafetyNetUnderwriting/`, its own resource in `Wayfinder.AppHost`) standing in for a
fictional insurer a caseworker sends the applicant's risk assessment to, with its own staff
worklist at `/queue`. The juggling-licence blueprint really does call out to it: a caseworker
reviewing a dangerous-props act sends the uploaded risk assessment across, waits on it (flagged
"Waiting" in their own worklist), and the insurer's decision comes back by webhook. See
[docs/guides/support-systems.md](./support-systems.md) for the full picture, and
[docs/demos/](../demos/) for a recording of the whole journey.

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
| Stage rendering | `Wayfinder.Rendering.GovUk`'s `GovUkComponentRenderer` — the shared package's own default rendering, registered as-is with zero overrides, driving hand-rolled server-side HTML pages that load the real `govuk-frontend` package (see below) | Ships its own Razor views/tag helpers — a complete GDS-style rendering layer, not something a host writes itself |
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
- `Wayfinder.ReferenceApp/service-blueprints/` — the seeds (`juggling-licence.json`, and
  `juggling-insurance-modeller.json` for the slider/stat-group/chart-driven premium modeller demo
  at `/premium` — see [Package Architecture](#package-architecture) for what renders that)
- `Wayfinder.ReferenceApp/Services/` — every custom implementation in the table above
- `Wayfinder.ReferenceApp/Services/SupportSystems/` — `SafetyNetUnderwritingClient`
  (`ISupportSystemClient`) and the real `SupportSystemDescriptor` registration for the third,
  support-processes lane — see [docs/guides/support-systems.md](./support-systems.md)
- `SafetyNetUnderwriting/` — the fictional insurer itself: a genuinely separate ASP.NET Core app
  (own `Wayfinder.AppHost` resource, not a library inside `Wayfinder.ReferenceApp`), with its own
  staff worklist at `/queue`
- `Wayfinder.ReferenceApp/wwwroot/` — just this app's own favicon/manifest branding now;
  **host-specific** assets only. `govuk-frontend` itself, and everything owned by a shared
  Wayfinder package (slider/stat-group/chart styling, calculation runtimes, the live-form
  runtime, the join-gateway poll script), lives in that package's own `wwwroot` instead — see
  below
- `Wayfinder.ReferenceApp.Tests/` — the Playwright suite (auth, the full citizen→caseworker→citizen
  handoff, the editor/authoring wiring, file upload, the premium modeller) — run single-worker,
  since the backend is one shared in-memory process with fixed demo identities, not per-test
  isolated. `Wayfinder.Editor.Client`'s own suite covers the support-system-call action's
  *authoring* UX (`support-system-action-editor.spec.ts`) — see
  [docs/skills/canvas-editor/SKILL.md](../skills/canvas-editor/SKILL.md) — since that needs no
  live SafetyNet Underwriting process, only the registered descriptor. The default
  `npm run test:playwright` here boots `Wayfinder.ReferenceApp` directly, without Aspire, so it
  can't exercise the real cross-process "send to insurer" round trip
  (`SafetyNetUnderwritingClient`'s `http://safetynet-underwriting` service-discovery address
  never resolves outside `Wayfinder.AppHost`) — that's what `npm run test:playwright:live` is
  for: `support-systems-live.spec.ts`, driven by `tests/support/live-app-host.ts`, which boots
  the real `Wayfinder.AppHost` stack (precedent: Umbraco.Prism's own
  `UmbracoPrism.Client/tests/support/live-app-host.ts`, proportionately leaner here — two plain
  in-memory apps, no Docker/Keycloak to wait on) and polls both resources' own HTTP endpoints
  until they're genuinely answering before any test runs against them. See
  [docs/guides/support-systems.md](./support-systems.md) for the worked example this spec
  actually drives, browser-to-browser, across both real apps.

## Package Architecture

**Wayfinder's components (rendering, the calculation language, the editor) are owned once, by
Wayfinder, and packaged so any host gets them automatically — never re-implemented or
hand-copied per host.** A host only ever supplies *its own* concerns on top: storage backends,
auth, business services (`ResolveServiceInputs`), and presentation chrome/branding. If something
looks like host-specific glue code but is actually describing behaviour for a component type or
expression grammar Wayfinder itself defines, that's a sign it's in the wrong project — see the
concrete example below.

`Wayfinder.Umbraco` is not a different set of components — it's the same shared packages,
wired up as a specific *implementation* of Wayfinder's extension points: an Umbraco backoffice
package (editor in the backoffice, uSync-tracked persistence, Umbraco-specific hosting). See the
extension-point table above for exactly how it differs from this reference app at the
implementation level, not the component level.

### Shared static web assets

`Wayfinder.Rendering.GovUk` and `Wayfinder.Editor` both ship CSS/JS as their own static web
assets — `Sdk="Microsoft.NET.Sdk.Web"`, `IsPackable`, and an explicit `StaticWebAssetBasePath`
(`/_content/{PackageId}/...`) in the `.csproj`. A referencing host gets these for free via
ASP.NET Core's standard static-web-assets middleware (`app.UseStaticFiles()`) — no hand-copying,
no `wwwroot` file to keep in sync across repos. This is a *Microsoft.NET.Sdk.Web* concern, not a
Razor/MVC one: the C# in these packages stays plain, framework-independent code either way.

The concrete history here is a worked cautionary example, not a hypothetical: the CSS and JS
enhancing `Wayfinder.Rendering.GovUk`'s own slider/stat-group/chart markup (no GOV.UK Design
System equivalent exists for these types) were originally hand-copied into
`Wayfinder.ReferenceApp/wwwroot`. The CSS was *also* independently duplicated in
[Umbraco.Prism](https://github.com/jonnymuir/Umbraco.Prism)'s own `components.css` — two repos
maintaining the same styling by hand, with no mechanism to notice drift between them. `wayfinder-
components.css` and `wayfinder-slider.js` now live in `Wayfinder.Rendering.GovUk/wwwroot/`,
served from `/_content/Wayfinder.Rendering.GovUk/`. Before adding a CSS/JS file to any host's own
`wwwroot`, ask: does this style or enhance a component type / behaviour Wayfinder itself defines?
If so, it belongs in the package that defines that type, not the host.

Same question, same answer, for two more things that had been quietly living in
`Wayfinder.ReferenceApp` despite describing Wayfinder's own behaviour rather than anything
host-specific:

- **The real `govuk-frontend` package itself** — CSS, JS, and fonts — is now vendored inside
  `Wayfinder.Rendering.GovUk/wwwroot/govuk-frontend/` (`Wayfinder.Rendering.GovUk/package.json`
  documents the exact version), not hand-copied per host. `GovUkComponentRenderer`'s generated
  markup targets a *specific* `govuk-frontend` version's class names and structure, so shipping
  the matching build alongside the renderer that assumes it keeps them permanently in lockstep —
  no host can accidentally load a mismatched version. One real wrinkle: `govuk-frontend.min.css`'s
  own `@font-face` rules hard-code an absolute `/assets/fonts/...` URL regardless of where the CSS
  itself is served from, so `Wayfinder.ReferenceApp/Program.cs` re-roots that one sub-path onto
  its own site root with a second `SubPathFileProvider`-backed `UseStaticFiles()` call — the exact
  same trick already used to serve `Wayfinder.Editor`'s compiled bundle at plain `/`, just applied
  to a font sub-path instead of a whole site root.
- **The join-gateway poll script and the govuk-frontend `initAll()` bootstrap** — previously two
  raw JavaScript strings hand-authored as C# constants inside `PageShell.cs` — are now real files,
  `wayfinder-poll.js` and `wayfinder-govuk-frontend-init.js`, shipped the same way. Neither had
  any host-specific logic in it: the poll script only ever reacts to
  `data-wayfinder-poll-interval-ms`, a Wayfinder-owned data attribute (`RenderWaiting` in
  `GovUkComponents.cs`), and the init script is govuk-frontend's own documented three-line
  quick-start, not something specific to this app.

### The calculation language has two runtimes, one canonical source

[The calculation language](./calculation-language.md) is evaluated by two independent
implementations checked against the same conformance suite
(`Wayfinder/calculation-fixtures/calculation-golden.json`):

- **C#** (`Wayfinder/Services/Calculations`) — authoritative; the engine only ever persists or
  branches on what this computes.
- **JavaScript** (`Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-calculations.js`) — for a host
  that wants the same expressions re-evaluated client-side, with no round-trip.

Both now live in Wayfinder itself, run against the same golden fixture in CI
(`.github/workflows/ci.yml`'s "JS calculation engine conformance" step). This wasn't always
true: the JS runtime was ported from an independent TypeScript implementation
([Umbraco.Prism](https://github.com/jonnymuir/Umbraco.Prism)'s own `calculation-engine.ts`,
mirroring the same grammar in a separate repo with no shared source, only a shared fixture file
to catch drift after the fact). Umbraco.Prism switching to consume Wayfinder's canonical version
instead of its own copy is a known, not-yet-done follow-up — flagged here so it isn't
rediscovered as a surprise later.

Using the client-side runtime never changes the engine's trust model — it's a preview
accelerator only. A host still only ever submits raw field inputs to `Advance`, which always
recomputes the calculation scope server-side from persisted `FieldValues`; nothing a client
claims to have calculated is itself ever trusted for a real decision.

### The live-form runtime

Running the maths client-side is only half the job — a stage's *markup* (stat-group values,
chart bars, `showWhen`-gated visibility) still needs to be re-rendered from that output. That
piece is `Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-live-form.js`, wired in automatically
for free by two things every host already gets: `ProcessManagerEngine.BuildLiveModel` (the
calculation set, input types/defaults, service-sourced values, populated into
`StepContent.Data["live"]` whenever a definition declares a `calculations` block — engine-owned,
not host-specific) and `GovUkComponentRenderer.RenderForm` embedding it as
`<script type="application/json" data-wayfinder-live-model>`. A host only has to load the script
itself (`/_content/Wayfinder.Rendering.GovUk/js/wayfinder-live-form.js`, `type="module"`) — see
`Wayfinder.ReferenceApp/Services/PageShell.cs`.

Same discovery as the calculation engine, a third time over: the real implementation already
existed — `prism-live-form.ts`, again only in
[Umbraco.Prism](https://github.com/jonnymuir/Umbraco.Prism), reading `[data-wayfinder-show-when]`
and `[data-wayfinder-stat-field]`/`[data-wayfinder-chart]` hooks `GovUkComponentRenderer` already
emits for exactly this purpose. Ported into `Wayfinder.Rendering.GovUk`, adapted from Umbraco.
Prism's own `fields[key]` form-naming convention to this package's `field:{key}` one
(`GovUk.FieldName`). Umbraco.Prism switching to consume this instead of its own copy is the same
kind of not-yet-done follow-up as the calculation engine above.

`/premium`'s own "Recalculate" button — a plain, fully declarative self-loop gateway
(`money-modeller.json`'s own documented pattern, see
[calculation-language.md](./calculation-language.md)) that any blueprint can still declare
without any client-side runtime at all — is gone from `juggling-insurance-modeller.json` now
that `wayfinder-live-form.js` makes it redundant for this specific demo: every input change
recalculates instantly, client-side, with zero network requests (see
`insurance-modeller.spec.ts`'s own "genuinely local" test, which asserts exactly that).

`wayfinder-live-recalculate.js` (the AJAX-automated version of clicking that button — never
computed anything itself, just fetched freshly-rendered HTML) is deleted, not just unloaded: once
`wayfinder-live-form.js` exists, nothing needs it. It required JavaScript exactly like the
live-form runtime does, but unlike the live-form runtime it still paid for a network round trip
on every change — there's no scenario where it beats *either* alternative (a plain declarative
button with no JS at all, or the live-form runtime with JS). Its one real justification would
have been calculation confidentiality — the live-form runtime embeds the actual `calculations`
expressions as page JSON, visible via view-source, where the AJAX version never shipped the
formulas client-side at all — but nothing in this repo has ever needed that, so it stayed
unbuilt-on hypothetical baggage rather than an active alternative. Recoverable from git history
if a real host ever needs exactly that trade-off.

## Accessibility

Every screen is expected to meet **WCAG 2.2 AA**, and that's enforced, not asserted:
`Wayfinder.ReferenceApp.Tests/tests/accessibility.spec.ts` runs axe-core across the whole citizen
journey (including a real server-rendered validation-error page), the caseworker queue, review and
waiting screens, and checks keyboard operability explicitly — focus order matching visual order,
`Space` toggling a checkbox, and a visible focus indicator.

**A Safari caveat worth knowing before you report a bug.** On macOS, Safari's *default* is to move
Tab only between text fields and pop-up menus — it skips links, buttons, checkboxes and radios.
Verified directly against WebKit on the event-details form: it tabs the five text inputs and
nothing else, skipping even the skip-link, while the identical markup tabs everything in Chromium
and Firefox. That's a user-agent preference that applies to every site on the web (GOV.UK
included) and page markup cannot override it. To test keyboard navigation properly in Safari,
enable **Safari → Settings → Advanced → "Press Tab to highlight each item on a webpage"**, or
macOS **System Settings → Keyboard → Keyboard navigation**.

axe catches roughly a third of WCAG issues — it cannot judge whether an error message is *useful*
or a heading structure *meaningful*, so it supplements manual testing rather than replacing it.
