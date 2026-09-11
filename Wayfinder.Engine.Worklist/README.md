# Wayfinder.Engine.Worklist

A default, optional caseworker worklist surface, server-rendered GOV.UK markup for the
filter/sort/search/paginated queue list (see docs/guides/queue-worklist-filtering.md), an item
review page, advance, and per-cursor pickup/putback (see docs/guides/work-allocation.md). A host
wires it up once with `AddWorklist()`/`MapWorklist(prefix)` — or, for a host whose own routing
model doesn't fit a mounted route group (an Umbraco Block Grid component, say), calls
`WorklistRenderer`'s rendering functions directly and supplies its own routes.

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

app.MapWorklist(prefix: "/caseworker/queue")
   .RequireAuthorization("Caseworker")
   .ValidateWayfinderAntiforgery();   // if these routes are cookie-authenticated — see below
```

If a host authenticates this surface with an ambient browser cookie, chain
`.ValidateWayfinderAntiforgery()` (from `Wayfinder.Engine.Http`) onto the group and register
`builder.Services.AddAntiforgery()` + `app.UseAntiforgery()`. The pickup/putback/advance forms
this package renders then carry a hidden `__RequestVerificationToken` automatically. See
[`Wayfinder.Engine.Http`](../Wayfinder.Engine.Http)'s README § CSRF. A bearer-token host needs
none of this.

`MapWorklist` maps five routes under `prefix`, all genuinely relative to it, every link, form
action, and redirect inside the package is built from the `prefix` a host passes in, never
hardcoded, so a host can mount this at any path it likes:

- `GET  {prefix}`, the list: status/sort/search filters, a paginated table, and a Pick up/Put back
  control per row.
- `GET  {prefix}/{blueprintKey}/{instanceId}`, the item review page.
- `POST {prefix}/{blueprintKey}/{instanceId}/advance`
- `POST {prefix}/{blueprintKey}/{instanceId}/pickup?cursorId=...`
- `POST {prefix}/{blueprintKey}/{instanceId}/putback?cursorId=...`

## Using `WorklistRenderer` directly

A host whose own routing model doesn't fit `MapWorklist`'s mounted route group — Umbraco's Block
Grid, for instance, where a "worklist" is one component embedded in an editor-composed page rather
than a standalone route tree — calls the same rendering functions `MapWorklist` itself uses,
supplying its own URLs and antiforgery token instead of a `prefix`:

```csharp
var (statuses, selectedStatuses, sort, pageIndex, size) = WorklistRenderer.ParseWorklistQuery(
    status: Request.Query["status"], sort: Request.Query["sort"], page: ..., pageSize: ...,
    statusFilterApplied: Request.Query["statusFilterApplied"], defaultPageSize: 20);

var envelope = processManager.GetQueueWorkItems(tenantId, userId, accessProfile, statuses, sort, q, pageIndex, size);

var body = WorklistRenderer.RenderWorklistBody(
    listUrl: myListUrl, itemUrlPrefix: myItemUrlPrefix, pageTitle: null, // null: the host's own page already has a heading
    envelope, selectedStatuses, sort, q, pageIndex, size,
    antiforgeryToken: myAntiforgery.GetAndStoreTokens(HttpContext).RequestToken);
```

`itemUrlPrefix` still has to resolve `{itemUrlPrefix}/{blueprintKey}/{instanceId}/pickup?cursorId=...`
/ `.../putback?...` to a real pickup/putback endpoint of the host's own, reading the `returnTo`
hidden field `RenderPickupPutbackControl` posts alongside `cursorId` — see `Wayfinder.Umbraco`'s
own worklist Block Grid component for a real, non-`MapWorklist` consumer.

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
