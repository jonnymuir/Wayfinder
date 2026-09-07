# Wayfinder.Engine.Http

The engine's own HTTP-request-processing glue a host otherwise hand-copies per route. Plain
functions, no routing of its own, a host calls these from inside its own minimal-API handlers,
the same way [`Wayfinder.Rendering.GovUk`](../Wayfinder.Rendering.GovUk)'s `GovUkStageJourney`
functions are called (see that package's own README for the sibling functions this one pairs
with, posted-form field coercion, file-download URL injection).

## Why a separate package from `Wayfinder.Rendering.GovUk`

`Wayfinder.Rendering.GovUk` deliberately has no dependency on `Wayfinder.Engine`, it only ever
touches `Wayfinder.Models.ServiceDesign` types plus its own rendering functions. File-upload
handling genuinely needs `IServiceRequestFileStorage` (`Wayfinder.Engine.Abstractions`), so it
lives here instead, keeping that dependency boundary honest rather than pulling the whole engine
into a package that otherwise works from any HTTP host.

## Usage

```csharp
var problems = await StageFileUploads.ApplyFileUploadsAsync(
    form, envelope.Render, instanceId, fileStorage, fieldValues);
```

Validates every `file-upload` field on the current stage against its own declared
`MaxSizeBytes`/`AcceptedFileTypes`, saves an accepted file via `IServiceRequestFileStorage`, and
writes the resulting `ServiceRequestFileReference` into `fieldValues`. A field with no file posted
this time round is left untouched entirely, so the engine's own merge preserves whatever reference
(if any) the instance already has stored. Returns one `ServiceRequestProblem` per rejected file,
empty means every posted file was accepted, or none were posted at all.

## CSRF: `WayfinderAntiforgery`

The `MapJourney`/`MapWorklist`/`MapBulkDatasetReview` route surfaces ship with no auth or
antiforgery opinion of their own. A host that authenticates them with an **ambient browser
cookie** must add CSRF protection, or a cross-site form/`fetch` can advance someone else's
application or pick up a caseworker's item. A host that authenticates them with a **bearer
token** (no ambient cookie) does not — CSRF does not apply there.

```csharp
builder.Services.AddAntiforgery();
// ...
app.UseAntiforgery();               // after UseAuthentication / UseAuthorization
// ...
app.MapWorklist(prefix: "/caseworker/queue")
   .RequireAuthorization("Caseworker")
   .ValidateWayfinderAntiforgery();
```

`ValidateWayfinderAntiforgery()` adds an endpoint filter that validates the ASP.NET antiforgery
token on every non-safe (`POST`/`PUT`/`PATCH`/`DELETE`) request to the group, returning `400`
when it is missing or invalid. The GET handlers in the journey/worklist packages then mint a
request token via `WayfinderAntiforgery.MintRequestVerificationToken(HttpContext)` and render it
— into each form as a hidden `__RequestVerificationToken` input, and into the bulk-data-review
component as `data-wayfinder-bulk-review-antiforgery-token` (its client sends the token back as a
`RequestVerificationToken` header). A host that never calls `ValidateWayfinderAntiforgery()` pays
nothing: `MintRequestVerificationToken` returns `null` when antiforgery services are absent, and
the forms carry no token field.
