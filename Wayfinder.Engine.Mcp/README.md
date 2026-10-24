# Wayfinder.Engine.Mcp

Exposes Wayfinder's service blueprint authoring (list, read, validate, save, simulate) as MCP
(Model Context Protocol) tools over HTTP, so an AI agent can call them directly. Built on
the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)'s HTTP
transport (`ModelContextProtocol.AspNetCore`).

This is a library, not a standalone process. A host adds `MapServiceBlueprintAuthoringMcp()`
to its own ASP.NET Core pipeline, alongside
[`MapServiceBlueprintAuthoringApi()`](https://github.com/jonnymuir/Wayfinder/tree/main/Wayfinder.Engine.Api). Both call the
same live `ServiceBlueprintAuthoringService`/`IServiceBlueprintSourceStore`, in process. That matters:
an MCP server running as a separate stdio process can't see an app's live state. Hosted this way,
a `save_service_blueprint` call reaches the running engine immediately, with no restart and no
separate process to keep track of.

[`Wayfinder.ReferenceApp`](https://github.com/jonnymuir/Wayfinder/tree/main/Wayfinder.ReferenceApp) (in this repo) is the reference
implementation. It calls both `MapServiceBlueprintAuthoringApi()` and
`MapServiceBlueprintAuthoringMcp()` against its own live store, which is all a real host app does
to expose this surface. See [the reference app guide](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/reference-app.md)
for the full picture.

## Tools

| Tool | Description |
|---|---|
| `list_service_blueprints` | List every service blueprint definition in the store (key + display name). |
| `read_service_blueprint` | Read a service blueprint definition by `definitionKey`. |
| `list_queue_capabilities` | List every queue this host has declared render capabilities for, and which component types each supports. A queue absent from the result is unrestricted (not this host's declared concern). |
| `list_component_types` | List every registered `Component` "type" discriminator (built-in and any toolkit extension's own), display name, category, property schema, and containment shape. The live source of truth behind every "type" string used in a stage's `components` array. |
| `validate_service_blueprint` | Check gateway routing, any `calculations` block, every component's own properties against its registered type, and (when the host declares queue render capabilities) that every component is supported by its state's queue, without saving. |
| `save_service_blueprint` | Validate and save. Invalid definitions are rejected, not saved. Visible to the live app immediately. |
| `list_design_notes` | The design notes on a blueprint (open by default), each with its anchor resolved to a label and the path it points at in the current version, or marked removed. |
| `add_design_note` | Pin a question or a flag to a stage, gateway, route, field or the whole blueprint, attributed to the design partner. |
| `resolve_design_note` | Mark a note resolved, with a sentence on what was done and the blueprint version it was done in. |
| `reply_to_design_note` | Reply to a note without resolving it, to ask the designer before acting. |
| `simulate_service_blueprint` | Dry-run a scripted sequence of actions with zero persistence. Returns `{ trace, calculations }`, the resulting state trace, plus the raw calculated field/series values per step (not just what's baked into rendered UI text). Accepts optional `mockServiceInputsJson` to resolve any `source: "service"` calculation field. |

## Resources

Alongside the tools, this project registers five MCP resources: the canonical authoring docs,
embedded from `docs/guides/` at build time, so any MCP client can fetch them with no repo
checkout.

| Resource URI | Content |
|---|---|
| `service-blueprint-docs://calculation-language` | [The Wayfinder Calculation Language](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/calculation-language.md), grammar, functions, tables/series, `showWhen`. |
| `service-blueprint-docs://authoring-guide` | [Reference Service Blueprint Contract](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/reference-service-blueprint-contract.md), the full `ServiceBlueprint` YAML shape. |
| `service-blueprint-docs://service-design-principles` | [Service Design Principles](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/service-design-principles.md), Double Diamond, the GOV.UK Service Standard, and Lou Downe's 15 principles of good services, industry-agnostic. |
| `service-blueprint-docs://ai-service-blueprint-authoring` | [AI-Ready Service Blueprint Authoring, Integrator Guide](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/ai-service-blueprint-authoring.md), how a host app wires this MCP surface into its own pipeline. |
| `service-blueprint-docs://extending-the-component-catalog` | [Extending the Component Catalog](https://github.com/jonnymuir/Wayfinder/blob/main/docs/guides/extending-the-component-catalog.md), how to register a genuinely new `Component` type. |

## Connect it to Claude Code

Start the app you want to author service blueprints against, find its URL (via the Aspire
dashboard, `Wayfinder.ReferenceApp`'s row has a labeled **"Service Blueprint Authoring MCP (HTTP)"**
link), then:

```
claude mcp add --transport http wayfinder-service-blueprint http://localhost:<port>/wayfinder/service-blueprint-authoring/mcp
```

Use the **HTTP** URL. The dashboard also has an HTTPS link, but Claude Code (like most MCP HTTP
clients) rejects the self-signed ASP.NET Core dev certificate with "unable to verify the first
certificate". Plain HTTP is fine here because it never leaves localhost. To use HTTPS, trust the
dev certificate in Node instead of turning certificate checks off:

```bash
dotnet dev-certs https --export-path ~/.aspnet/dev-cert.pem --format Pem --no-password
rm ~/.aspnet/dev-cert.key        # the export also writes the private key; Node only needs the certificate
export NODE_EXTRA_CA_CERTS=~/.aspnet/dev-cert.pem
```

The transport is Streamable HTTP; the older SSE transport is not supported.

## Auth

`MapServiceBlueprintAuthoringMcp()` is deny-by-default, like `MapServiceBlueprintAuthoringApi()`:
it requires an authenticated caller, or the policy you name with `authorizationPolicy`, unless the
host opts out with `allowAnonymous: true`. A client then passes credentials at registration:

```
claude mcp add --transport http wayfinder-service-blueprint <url> --header "Authorization: Bearer <token>"
```

The reference app (`Wayfinder.ReferenceApp`) opts out with `allowAnonymous: true`, as it does for
its editor endpoints, because it is a local demo host that never ships.

## A note on tool selection

If you're running Claude Code from within a checkout of this repo itself (or any
repo that happens to contain the same seed/source files the connected app was built
from), the agent has ordinary file tools alongside these MCP tools. Nothing stops it from
finding and editing a seed YAML file directly instead of calling `save_service_blueprint`. Doing so has no effect on the running app (seed files are only read at
process startup) and skips validation entirely. The tool descriptions call this out
explicitly, but if you want a clean test of tool selection, run Claude Code from a
directory with no copy of the host app's source in it. The MCP tools stay reachable over HTTP
from any working directory; there's just nothing else to find.
