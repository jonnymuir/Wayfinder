# Wayfinder.Editor.Http

The server half of the load/save contract that
[`Wayfinder.Editor`](https://github.com/jonnymuir/Wayfinder/tree/main/Wayfinder.Editor)'s packaged
`service-blueprint-editor.html` page expects. The contract comes from the page's bundled
`MockBusinessAppServiceBlueprintSource` example, a reference integration to fork into your own
host rather than part of the editor itself. A host that just wants the packaged page to work out
of the box maps this instead of hand-writing those routes.

## Usage

```csharp
app.MapMockBusinessAppServiceBlueprints(authorizationPolicy: "BlueprintsAdmin"); // prefix defaults to "/mockapp/service-blueprints"
```

Deny by default, like `Wayfinder.Engine.Api`'s/`Wayfinder.Engine.Mcp`'s own mapping calls: these
routes can overwrite every blueprint, so they require an authenticated caller (or the policy you
name) unless the host writes `allowAnonymous: true`. There's no `Add...()` call: it needs only
`ServiceBlueprintAuthoringService`, which the `AddServiceBlueprintAuthoring()` call every
authoring surface (API, MCP, this) requires has already registered.

The contract (matching `MockBusinessAppServiceBlueprintSource` exactly):

- `GET  {prefix}` → JSON array of `{ definitionKey, displayName }`.
- `GET  {prefix}/{key}` → the raw `ServiceBlueprint` JSON, or `404`.
- `PUT  {prefix}/{key}` → body is a full `ServiceBlueprint`; `204` on save, `409` +
  `ServiceBlueprintSaveOutcome` body on a version conflict, `400` otherwise.

## Why a separate package from `Wayfinder.Editor`

`Wayfinder.Editor` is deliberately pure static assets, zero ASP.NET Core routing, zero
`Wayfinder.Engine` dependency, just the compiled editor bundle. This needs
`ServiceBlueprintAuthoringService`, so it gets its own package rather than pulling that dependency
into the asset-only one, the same reasoning `Wayfinder.Engine.Http` split off from
`Wayfinder.Rendering.GovUk` for.
