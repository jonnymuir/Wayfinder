# Wayfinder.Editor.Http

The backend half of a contract [`Wayfinder.Editor`](../Wayfinder.Editor)'s packaged
`service-blueprint-editor.html` demo page expects, not from the editor package itself, but from
its bundled `MockBusinessAppServiceBlueprintSource` TS example (a documented "reference
implementation, fork this into your own host" integration, not part of the editor's shipped
bundle). A host that just wants the packaged demo page to work out of the box maps this instead of
hand-copying that example's expected routes itself.

## Usage

```csharp
app.MapMockBusinessAppServiceBlueprints(); // defaults to prefix "/mockapp/service-blueprints"
```

Anonymous by default, same reasoning as `Wayfinder.Engine.Api`'s/`Wayfinder.Engine.Mcp`'s own
mapping calls: a real host chains its own `.RequireAuthorization()` if it wants one. No
`Add...()`, it needs nothing beyond `ServiceBlueprintAuthoringService`, already registered by the
`AddServiceBlueprintAuthoring()` call any authoring surface (API/MCP/this) requires.

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
