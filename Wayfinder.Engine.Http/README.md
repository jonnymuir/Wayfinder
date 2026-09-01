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
