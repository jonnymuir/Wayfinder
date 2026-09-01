# Wayfinder: Project Guide for Claude Code

## What this is

Wayfinder is a service blueprint / service-design engine: framework-agnostic domain
model, a total-expression calculation engine, a state-machine process engine (queues, gateways,
instance persistence), and a compiled visual editor web component. No Umbraco, no ASP.NET Core
MVC, no hosting assumptions. A host layers its own tenancy, auth, and rendering on top.

The domain model *is* the [Nielsen Norman Group service blueprint](https://www.nngroup.com/articles/service-blueprints-definition/)
(Sarah Gibbons, 2017) made executable: customer actions, frontstage, backstage and support
processes, separated by the lines of interaction, visibility and internal interaction. NN/g's
horizontal lanes are Wayfinder's `queues` (the editor draws them as vertical columns).
A `stage` is a step in the journey and belongs to a queue, which is what puts it in a lane.
A `gateway` is the route from one stage to the next, any lane to any lane, and it also carries
that route's declarative rules: split or join, waiting information, conditions. "Support
Systems" is NN/g's support-processes lane. Use that vocabulary in code, API and docs, and cite
NN/g and GDS/GOV.UK (never other workflow products) when explaining why the model looks the way
it does.

Extracted from [Umbraco Prism](https://github.com/jonnymuir/Umbraco.Prism), which is now a
consumer. [`Wayfinder.Umbraco`](https://github.com/jonnymuir/Wayfinder.Umbraco) is the
Umbraco-hosted implementation. All three repos publish to nuget.org only, with no GitHub
Packages feed.

Solo developer project. Work directly on `main` for trivial fixes; feature branches + PRs for
substantive changes.

## Projects

| Project | Ships as | Purpose |
|---|---|---|
| `Wayfinder` | `Wayfinder` | Domain model (`ServiceBlueprint`, envelopes), the calculation engine, the sanitizer interface. Zero framework dependency. |
| `Wayfinder.Engine` | `Wayfinder.Engine` | The authoritative in-process `IProcessManager`: queue routing, gateway evaluation, instance persistence, support-systems, bulk data. |
| `Wayfinder.Engine.Api` | `Wayfinder.Engine.Api` | `MapServiceBlueprintAuthoringApi()`: REST authoring surface over `ServiceBlueprintAuthoringService`. |
| `Wayfinder.Engine.Mcp` | `Wayfinder.Engine.Mcp` | `MapServiceBlueprintAuthoringMcp()`: the same authoring operations as MCP-over-HTTP. Uses `ModelContextProtocol.AspNetCore`. |
| `Wayfinder.Engine.Http` / `.Journey` / `.Worklist` | *(not packed individually, internal helpers)* | HTTP glue (`StageFileUploads`), single-actor journey surface, caseworker worklist surface. |
| `Wayfinder.Rendering.GovUk` | `Wayfinder.Rendering.GovUk` | Real GOV.UK Design System rendering (vendored `govuk-frontend`), the component/field catalog, calc-driven live components. |
| `Wayfinder.Editor` / `Wayfinder.Editor.Http` | `Wayfinder.Editor` | The compiled service-blueprint editor web component + its host-side REST glue. TS source in `Wayfinder.Editor.Client`. |
| `Wayfinder.ReferenceApp` + `Wayfinder.AppHost` | *(never ships)* | Self-contained Aspire host wiring every package, the reference for how little a real host needs. |
| `Wayfinder.Tests` | *(never ships)* | xUnit suite for the engine + domain + rendering. |
| `Wayfinder.ReferenceApp.Tests` | *(never ships)* | Playwright specs against the reference app (`playwright.config.ts`, `.live.config.ts`, `.demo.config.ts`). |

## Build and test

```bash
# The editor bundles pack as static web assets, so they must exist on disk before dotnet build/pack
cd Wayfinder.Editor.Client && npm ci && npm run build && cd ..

dotnet build Wayfinder.slnx -c Release
dotnet test  Wayfinder.slnx -c Release --no-build
dotnet pack  Wayfinder.slnx -c Release --no-build -o ./artifacts

# TypeScript conformance + editor tests (in Wayfinder.Editor.Client)
node scripts/run-calculation-runtime-tests.mjs   # C#/TS calc parity against the shared golden fixtures
npm run test:component-schema
npx playwright test --reporter=line               # editor Playwright specs

# Reference app (Aspire)
dotnet run --project Wayfinder.AppHost
```

There is **no `.sln`**. Everything is `Wayfinder.slnx`.

CI (`.github/workflows/ci.yml`) runs the editor client build → `dotnet` restore/build/test/pack →
the TS conformance scripts → storybook a11y/interaction → editor Playwright. `Wayfinder.ReferenceApp.Tests`
Playwright specs run via their own configs.

## Releasing

Four packages release **in lockstep**: `Wayfinder`, `Wayfinder.Engine`, `Wayfinder.Engine.Api`,
`Wayfinder.Engine.Mcp`. Bump every one of their `<Version>` together before tagging.
`Wayfinder.Rendering.GovUk` and `Wayfinder.Editor` version independently. `package-release.yml`
fires on a `v{version}` tag, verifies the four csproj `<Version>` match the tag, packs, and
pushes to nuget.org via Trusted Publishing. NuGet indexing lags a few minutes; verify against a
local feed if a downstream needs the new version immediately.

Downstream chain: a `Wayfinder` release → bump `Wayfinder.Umbraco`'s package refs → its release →
bump `Umbraco.Prism`'s. See `Wayfinder.Umbraco/CLAUDE.md`.

## Key conventions

### Testing: behavioural contracts, not implementation mirrors

Every test answers *"what should happen, observed from outside this unit?"*, never *"what does
the code do internally?"*. Adapted from the Umbraco.Prism squad's Tester charter.

1. **Test behaviour through public seams.** `IProcessManager` methods, an authoring-service call,
   a rendered HTML fragment, an MCP/REST response, the visible editor. If reaching the thing
   needs `internal` + `InternalsVisibleTo`, that's the signal you're at the wrong level.
2. **`InternalsVisibleTo` is a smell, not a tool.** Default to none.
3. **Prefer real in-memory implementations over mocking frameworks.** `Wayfinder.Tests` has no
   Moq dependency by design. Use `InMemoryServiceRequestStore`, `SingleDefinitionServiceBlueprintStore`,
   `PassthroughContentSanitizer`, `NullLogger`, and the `ServiceBlueprintSimulationRunner` for
   scripted journeys. Assert on outputs and returned envelopes, not private state.
4. **A behaviour-preserving refactor must not turn a test red.** Rename a private, restructure a
   DOM node, rename a CSS class → tests stay green.
5. **Name tests as behaviours, one per test.**
6. **Business maths lives in the blueprint's `calculations` block**, verified by the shared
   `Wayfinder/calculation-fixtures/` golden fixtures, run by both `CalculationGoldenTests` (C#)
   and `node scripts/run-calculation-runtime-tests.mjs` (TS). Change either evaluator only
   alongside those fixtures. Never hand-write domain maths in a host service or component.
7. **Prefer the coarsest fast+deterministic test.** A domain/engine test with real in-memory
   stores; a rendering test asserting the produced HTML; a `ServiceBlueprintSimulationRunner`
   trace. Reserve `Wayfinder.ReferenceApp.Tests` live Playwright runs for behaviour that only
   emerges from a booted host, and keep those few.
8. **Playwright:** semantic selectors only: `getByRole`, `getByLabel`, `getByText`, `aria-*`,
   `data-*` hooks. Never CSS classes or web-component tag names. Wait for a visible loaded
   indicator before reading values. For the editor's shadow DOM, target the open shadow root's
   semantic elements, not host internals.
9. **Keep every suite green before a PR** (`dotnet test`, the TS conformance scripts, the
   relevant Playwright configs).

### Security: non-negotiable

Adapted from the Umbraco.Prism squad's Copper mandate (tenant-isolation and auth-threat
reduction). Security correctness is a release gate, not a follow-up, held to the same standing
this project gives behavioural testing.

1. **Auth and trust-chain flows are spec-exact.** OAuth 2.0 / OIDC / PKCE / RFC 8414 / RFC 9207
   / RFC 9728 token, claim and discovery handling follows the RFC, with no fabricated
   identifiers, no "works for now" shims, no deviation for convenience. If the spec and an easier
   path conflict, the spec wins or the work stops and the trade-off is raised explicitly.
2. **The framework-agnostic core takes no auth or tenant bypass.** Identity, tenancy and
   authorization enter only through a host-supplied seam (`ResolveServiceInputs`,
   `IServiceRequestStore`, a policy the host registers). Never a hardcoded actor, queue, tenant
   or allow-all default in `Wayfinder` / `Wayfinder.Engine`.
3. **Deny by default at every new seam.** A `Wayfinder.Engine.Api` route or a
   `Wayfinder.Engine.Mcp` tool ships with an explicit authorization requirement, documented at
   the call site; anonymous access carries a written reason in the code.
4. **The calculation language stays total and side-effect-free**: no eval, no loops, no host
   callouts. That is a security property (untrusted blueprint input is evaluated), not only a
   design choice.
5. **No secrets in source, committed config, logs, or test fixtures** (golden fixtures
   included).
6. **Evaluate every change through the CIA lens**: confidentiality (cross-actor / cross-tenant
   leakage), integrity (can input forge state or skip a gateway), availability (can input force
   unbounded work). Anything that moves the threat surface is called out in the commit body and
   the PR description.
7. **Define a security regression check for any boundary you touch**: a behavioural test that
   goes red if the isolation or the validation regresses, per the testing rules above.
8. **Report security findings plainly.** No minimising language: "just a hack", "edge case",
   "couldn't survive". Name the defect and its impact.

### Branch policy

Feature branches + PRs for substantive changes: `{type}/{kebab-slug}`. Direct commits to `main`
for trivial fixes only.

### Commit conventions

[Conventional Commits](https://www.conventionalcommits.org/). `feat:` = minor, `fix:`/`perf:`/
`refactor:`/`test:`/`chore:`/`docs:` = patch, `feat!:` or a `BREAKING CHANGE:` body line = major.

### Code style

- No speculative abstractions. Solve the problem at hand.
- Comments only where the *why* is non-obvious; match the density of the surrounding file.
- Framework-agnostic core: `Wayfinder` and `Wayfinder.Engine` take no dependency on ASP.NET Core
  MVC, Umbraco, or a specific host. Host concerns enter via an interface or a callback the host
  supplies (`ResolveServiceInputs`, `IServiceRequestStore`, `IServiceRequestFileStorage`).
- **No duplication across packages**: one set of C# wrappers, one CSS/JS payload, one
  grammar/live-form runtime, one `govuk-frontend` vendoring. Use the real `govuk-frontend`
  toolkit (npm package / CSS / JS / markup), verified from the actual package source, never a
  lookalike.
- The calculation language is total and side-effect-free (no eval, no loops). Keep it that way.
