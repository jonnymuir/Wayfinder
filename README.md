# Wayfinder

A GDS-style service blueprint / service-design engine — domain model, calculation engine,
state-machine engine, and a compiled visual editor web component. Framework-agnostic: no
Umbraco, no ASP.NET Core MVC, no hosting assumptions baked in.

Wayfinder was extracted from [Umbraco Prism](https://github.com/jonnymuir/Umbraco.Prism),
which is now a consumer of these packages rather than their owner. A host application —
Umbraco-based or otherwise — layers its own tenancy, auth, and rendering opinions on top.
[`Wayfinder.Umbraco`](https://github.com/jonnymuir/Wayfinder.Umbraco) is the Umbraco-hosted
implementation Prism itself uses.

## Packages

| Package | Purpose |
|---|---|
| `Wayfinder` | Core domain models (`ServiceBlueprint`, `ServiceRequestResponseEnvelope`, etc.), the declarative calculation engine, and the sanitizer interface. Zero framework dependency. |
| `Wayfinder.Engine` | The service blueprint state-machine engine — queue routing, gateway evaluation, request persistence. |
| `Wayfinder.Engine.Api` | REST toolkit (`MapServiceBlueprintAuthoringApi()`) exposing service blueprint authoring — list/read/validate/save/simulate — over HTTP for any ASP.NET Core host. |
| `Wayfinder.Engine.Mcp` | MCP-over-HTTP toolkit (`MapServiceBlueprintAuthoringMcp()`) — the same authoring surface as MCP tools for AI agents. |

## The service blueprint model

A `ServiceBlueprint` describes a journey as `queues` (named work queues), `stages` (each
owning its own `routes`), and `gateways` (first-class Split/Join routing nodes — a stage's
routes must always target a gateway, never another stage directly). See
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

MIT — see [LICENSE](LICENSE).
