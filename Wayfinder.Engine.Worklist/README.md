# Wayfinder.Engine.Worklist

A default, optional caseworker worklist surface, server-rendered GOV.UK markup for the
filter/sort/search/paginated queue list (see docs/guides/queue-worklist-filtering.md), an item
review page, advance, and per-cursor pickup/putback (see docs/guides/work-allocation.md). A host
wires it up once, or ignores this package entirely and hand-writes the same routes itself, as
`Wayfinder.ReferenceApp` originally did.

## Usage

```csharp
builder.Services.AddWorklist(options =>
{
    options.ResolveTenantId = _ => "my-tenant";
    options.ResolveAccessProfile = ctx => MyActors.ProfileForCaseworkerUser(GetUserId(ctx.User));
    options.RenderPage = (title, body, ctx) => PageShell.Render(title, body, ctx.User);
    options.WorklistPageTitle = "Caseworker queue";
    options.ReviewPageTitle = "Review application";
});

// ...

app.MapWorklist(prefix: "/caseworker/queue").RequireAuthorization("Caseworker");
```

`MapWorklist` maps five routes under `prefix`, all genuinely relative to it, every link, form
action, and redirect inside the package is built from the `prefix` a host passes in, never
hardcoded, so a host can mount this at any path it likes:

- `GET  {prefix}`, the list: status/sort/search filters, a paginated table, and a Pick up/Put back
  control per row.
- `GET  {prefix}/{blueprintKey}/{instanceId}`, the item review page.
- `POST {prefix}/{blueprintKey}/{instanceId}/advance`
- `POST {prefix}/{blueprintKey}/{instanceId}/pickup?cursorId=...`
- `POST {prefix}/{blueprintKey}/{instanceId}/putback?cursorId=...`

## What's deliberately left out

File-download and bulk-dataset REST routes stay hand-wired on the host, this package only builds
URLs pointing at them (via `Wayfinder.Rendering.GovUk`'s `WithFileDownloadUrls`/
`WithBulkDatasetApiUrls`), assuming a host maps its own such routes under the same `prefix`. This
package owns zero page chrome: `RenderPage` is the escape hatch every response is wrapped through.

## Why a separate package

Sits above both `Wayfinder.Rendering.GovUk` (`GovUkStageJourney`'s journey rendering and posted-form
coercion) and `Wayfinder.Engine.Http` (`StageFileUploads`), this package's own job is purely
wiring those together into real ASP.NET Core routes against `IProcessManager`, the same
`Add.../Map...` shape `Wayfinder.Engine.Api` and `Wayfinder.Engine.Mcp` already use.
