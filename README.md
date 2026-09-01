<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/wordmark-dark.png">
  <img src="assets/wordmark-light.png" alt="Wayfinder" height="56">
</picture>

[![CI](https://github.com/jonnymuir/Wayfinder/actions/workflows/ci.yml/badge.svg)](https://github.com/jonnymuir/Wayfinder/actions/workflows/ci.yml)
[![Wayfinder](https://img.shields.io/nuget/v/Wayfinder.svg?label=Wayfinder)](https://www.nuget.org/packages/Wayfinder)
[![Wayfinder.Engine](https://img.shields.io/nuget/v/Wayfinder.Engine.svg?label=Wayfinder.Engine)](https://www.nuget.org/packages/Wayfinder.Engine)
[![Wayfinder.Editor](https://img.shields.io/nuget/v/Wayfinder.Editor.svg?label=Wayfinder.Editor)](https://www.nuget.org/packages/Wayfinder.Editor)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A service blueprint / service-design engine: domain model, calculation engine,
state-machine engine, and a compiled visual editor web component. Framework-agnostic, with no
Umbraco, no ASP.NET Core MVC, and no hosting assumptions baked in.

The domain model is the service blueprint as the
[Nielsen Norman Group defines it](https://www.nngroup.com/articles/service-blueprints-definition/):
a user journey laid out across customer actions, frontstage, backstage, and support processes,
divided by the lines of interaction, visibility, and internal interaction. Wayfinder makes that
model executable, and delivers journeys to
[GDS Service Standard](https://www.gov.uk/service-manual/service-standard) practice with the
real GOV.UK Design System.

Wayfinder was extracted from [Umbraco Prism](https://github.com/jonnymuir/Umbraco.Prism),
which is now a consumer of these packages rather than their owner. A host application,
Umbraco-based or otherwise, layers its own tenancy, auth, and rendering opinions on top.
[`Wayfinder.Umbraco`](https://github.com/jonnymuir/Wayfinder.Umbraco) is the Umbraco-hosted
implementation Prism itself uses.

## See it running

`Wayfinder.AppHost` + `Wayfinder.ReferenceApp` is a small, self-contained .NET Aspire host in
this repo, with every package wired together, real GOV.UK Design System rendering, a demo login,
and a seeded "apply for a licence to hold a juggling event" journey. It's the fastest way to
see what a working Wayfinder host actually looks like, and exactly how little wiring a real
host (like `Wayfinder.Umbraco`) collapses into. Run it with
`dotnet run --project Wayfinder.AppHost`, or the "C#: Aspire (Full Stack)" launch config in
VS Code. See [`docs/guides/reference-app.md`](docs/guides/reference-app.md) for what it
implements, how the demo blueprint is seeded from JSON and only saved in memory, and what a
real host does differently.

## Packages

| Package | Purpose |
|---|---|
| `Wayfinder` | Core domain models (`ServiceBlueprint`, `ServiceRequestResponseEnvelope`, etc.), the declarative calculation engine, and the sanitizer interface. Zero framework dependency. |
| `Wayfinder.Engine` | The service blueprint state-machine engine: queue routing, gateway evaluation, request persistence. |
| `Wayfinder.Engine.Api` | REST toolkit (`MapServiceBlueprintAuthoringApi()`) exposing service blueprint authoring (list/read/validate/save/simulate) over HTTP for any ASP.NET Core host. |
| `Wayfinder.Engine.Mcp` | MCP-over-HTTP toolkit (`MapServiceBlueprintAuthoringMcp()`): the same authoring surface as MCP tools for AI agents. |

## The service blueprint model

Wayfinder implements the
[Nielsen Norman Group service blueprint](https://www.nngroup.com/articles/service-blueprints-definition/)
(Sarah Gibbons, 2017) as a runnable artefact. In that model a user's journey is laid out across
horizontal lanes, divided by the lines of interaction, visibility, and internal interaction.
Those lanes are a blueprint's `queues`, one for each team or system that does the work. A
`stage` is a step in the journey. Every stage sits in a queue, and the queue is what places it
in a lane, so a stage is a stage whether it happens frontstage or backstage. A `gateway` is the
route from one stage to the next, from any lane to any lane. Alongside the route a gateway
carries the declarative rules for it: whether to split or join, waiting information, and the
conditions that choose a path. Support Systems is NN/g's support-processes lane made
first-class.

![Wayfinder's service blueprint model: NN/g's horizontal lanes and three lines of separation, and how a ServiceBlueprint's queues, stages, and gateways map onto them.](assets/service-blueprint-model.svg)

*The model is the [Nielsen Norman Group service blueprint](https://www.nngroup.com/articles/service-blueprints-definition/)
(Sarah Gibbons, 2017). See the article for Gibbons' own worked example.*

A `ServiceBlueprint` describes a journey as `queues` (named work queues), `stages` (each
owning its own `routes`), and `gateways` (first-class Split/Join routing nodes that a stage's
routes always target, never another stage directly). See
[`docs/guides/reference-service-blueprint-contract.md`](docs/guides/reference-service-blueprint-contract.md)
for the full authoring schema, and
[`docs/guides/calculation-language.md`](docs/guides/calculation-language.md) for the
declarative expression language used in `calculations` and `showWhen`.

## AI-ready authoring

Service blueprint authoring is exposed to AI agents (Claude Code or any MCP client) the
same way it's exposed to a human editor: as a toolkit a host app wires into its own
pipeline. `Wayfinder.Engine.Api` and `Wayfinder.Engine.Mcp` map the same
list/read/validate/save/simulate operations as REST and MCP-over-HTTP respectively, both
calling straight into a host's live `Wayfinder.Engine` in-process. See
[`docs/guides/ai-service-blueprint-authoring.md`](docs/guides/ai-service-blueprint-authoring.md).

## Building

```bash
dotnet build Wayfinder.slnx
dotnet pack Wayfinder.slnx
```

## License

MIT. See [LICENSE](LICENSE).
