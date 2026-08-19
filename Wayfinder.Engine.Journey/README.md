# Wayfinder.Engine.Journey

A default, optional single-actor journey surface — server-rendered GOV.UK markup for "get my
current stage, advance it" against one blueprint. The applicant/citizen counterpart to
[`Wayfinder.Engine.Worklist`](../Wayfinder.Engine.Worklist)'s multi-item caseworker queue: one
actor, one instance, one page — no list, no pickup/putback, no separate advance sub-route.

## Usage

```csharp
builder.Services.AddJourney(options =>
{
    options.ResolveTenantId = _ => "my-tenant";
    options.ResolveAccessProfile = _ => MyActors.CitizenProfile();
    options.RenderPage = (title, body, ctx) => PageShell.Render(title, body, ctx.User);
});

// ...

app.MapJourney("/apply", "my-blueprint-key", "Apply for a thing").RequireAuthorization("Applicant");
app.MapJourney("/premium", "another-blueprint-key", "Model a premium").RequireAuthorization("Applicant");
```

`AddJourney` is called once — the tenant/actor/page-chrome resolution is normally the same across
every journey a host maps. `MapJourney` is called once per blueprint a host wants a self-service
journey for, each with its own `prefix`, `blueprintKey`, and page title.

`MapJourney` maps two routes under `prefix`, both the *same* URL — GET to view the current stage,
POST to advance it (POST-redirect-GET back to the same URL, so a reload or a second tab never
resubmits a stale `stateVersion`):

- `GET  {prefix}`
- `POST {prefix}`

## Why a separate package from `Wayfinder.Engine.Worklist`

Same underlying building blocks (`GovUkStageJourney`'s rendering/form-coercion,
`Wayfinder.Engine.Http`'s file-upload handling, `IProcessManager`), but a genuinely different shape
— one instance per actor rather than a shared queue of many — so it gets its own package rather
than an awkward "worklist, but sometimes there's no list" branch inside that one.
