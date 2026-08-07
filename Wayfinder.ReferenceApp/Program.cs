using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Api;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Mcp;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.ReferenceApp.Services;
using Wayfinder.Rendering.GovUk;
using Wayfinder.Services.Sanitization;

// The seeded demo blueprints' keys — see service-blueprints/juggling-licence.json and
// service-blueprints/juggling-insurance-modeller.json.
const string JugglingLicenceDefinitionKey = "juggling-licence";
const string InsuranceModellerDefinitionKey = "juggling-insurance-modeller";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ── Auth: a hand-rolled in-memory cookie login, deliberately not OIDC/Keycloak — see
// Services/DemoUsers.cs for why. Two roles, one per actor lane this host demonstrates.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        options.Cookie.Name = "wayfinder-reference-app";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Applicant", policy => policy.RequireRole(DemoUsers.ApplicantRole));
    options.AddPolicy("Caseworker", policy => policy.RequireRole(DemoUsers.CaseworkerRole));
});

// ── Wayfinder wiring: the seed blueprint is a plain JSON file on disk (service-blueprints/) —
// the same FilesystemServiceBlueprintStore any real host uses, loaded once at startup. Runtime
// *instance* state stays in-memory (see InMemoryRuntimeServiceBlueprintSourceStore's remarks),
// so this host is still transient — Playwright resets it between tests via
// DELETE /api/test/reset instead of restarting the process.
builder.Services.AddSingleton<IServiceContentSanitizer, PassthroughSanitizer>();
builder.Services.AddSingleton<IServiceBlueprintStore>(
    _ => new FilesystemServiceBlueprintStore(Path.Combine(builder.Environment.ContentRootPath, "service-blueprints")));
builder.Services.AddSingleton<IQueueCapabilitiesProvider>(ReferenceActors.CapabilitiesProvider());
builder.Services.AddSingleton<IServiceRequestFileStorage, InMemoryServiceRequestFileStorage>();

// Wayfinder.Rendering.GovUk's built-in catalog covers every component/field type out of the
// box — this reference app registers zero overrides, the "simple by default" case. A host that
// wants to customize one type calls renderer.RegisterComponent/RegisterField after resolving it
// here, no Razor/ViewEngine ceremony required.
builder.Services.AddSingleton<GovUkComponentRenderer>();

builder.Services.AddSingleton(sp => new ProcessManagerEngine(
    sp.GetRequiredService<ILogger<ProcessManagerEngine>>(),
    sp.GetRequiredService<IServiceBlueprintStore>(),
    sp.GetRequiredService<IServiceContentSanitizer>()));
builder.Services.AddSingleton<IProcessManager>(sp => sp.GetRequiredService<ProcessManagerEngine>());

// The editor / REST / MCP authoring surface and the `/apply` + `/caseworker` request-processing
// surface share this one store, so a save from any authoring surface is immediately live for
// the next citizen/caseworker request — see InMemoryRuntimeServiceBlueprintSourceStore's remarks.
builder.Services.AddSingleton<InMemoryRuntimeServiceBlueprintSourceStore>();
builder.Services.AddSingleton<IServiceBlueprintSourceStore>(sp => sp.GetRequiredService<InMemoryRuntimeServiceBlueprintSourceStore>());
builder.Services.AddServiceBlueprintAuthoring();
builder.Services.AddServiceBlueprintAuthoringMcp();

var app = builder.Build();

app.MapDefaultEndpoints();

// This app's own wwwroot — the real govuk-frontend compiled CSS/JS/fonts/images vendored in
// (see Wayfinder.ReferenceApp/package.json and PageShell.cs), not a lookalike stylesheet.
app.UseStaticFiles();

// Serve Wayfinder.Editor's compiled service-blueprint-editor.html + JS/CSS assets at web root —
// its own build emits root-relative asset references, so it must be served from "/", not the
// package's default "_content/Wayfinder.Editor/dist/" prefix.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new SubPathFileProvider(app.Environment.WebRootFileProvider, "_content/Wayfinder.Editor/dist"),
    RequestPath = "",
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/service-blueprint-editor", (HttpRequest request) =>
{
    var key = request.Query["serviceBlueprint"].ToString();
    var target = string.IsNullOrWhiteSpace(key) ? JugglingLicenceDefinitionKey : key;
    return Results.Redirect($"/service-blueprint-editor.html?serviceBlueprint={Uri.EscapeDataString(target)}");
});

// Anonymous by design, same convention documented in Wayfinder.Engine.Api/README.md: a real
// host chains its own .RequireAuthorization() onto these route groups. This reference app
// demonstrates the auth boundary on the citizen/caseworker screens below instead, which is
// where it actually matters for a real deployment.
app.MapServiceBlueprintAuthoringApi();
app.MapServiceBlueprintAuthoringMcp();

// Wayfinder.Editor's packaged demo page (service-blueprint-editor.html, compiled from
// Wayfinder.Editor.Client) talks to a `/mockapp/service-blueprints/*` contract — the shape
// its bundled MockBusinessAppServiceBlueprintSource example expects — rather than the
// `/wayfinder/service-blueprint-authoring/*` routes above. This host implements that
// contract against the same live ServiceBlueprintAuthoringService so the packaged editor
// works out of the box without forking Wayfinder.Editor.Client's build. Anonymous, same
// reasoning as the authoring API/MCP above.
app.MapGet("/mockapp/service-blueprints", async (ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
    Results.Json(await authoring.ListAsync(ct)));

app.MapGet("/mockapp/service-blueprints/{key}", async (string key, ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
{
    var blueprint = await authoring.ReadAsync(key, ct);
    return blueprint is null ? Results.NotFound() : Results.Json(blueprint);
});

app.MapPut("/mockapp/service-blueprints/{key}", async (string key, HttpContext ctx, ServiceBlueprintAuthoringService authoring, CancellationToken ct) =>
{
    var blueprint = await ctx.Request.ReadFromJsonAsync<ServiceBlueprint>(ct);
    if (blueprint is null || !string.Equals(blueprint.DefinitionKey, key, StringComparison.Ordinal))
    {
        return Results.BadRequest();
    }

    var outcome = await authoring.SaveAsync(blueprint, blueprint.Version, ct);
    return outcome.Status switch
    {
        ServiceBlueprintSaveStatus.Saved => Results.NoContent(),
        ServiceBlueprintSaveStatus.Conflict => Results.Conflict(outcome),
        _ => Results.BadRequest(outcome)
    };
});

app.MapGet("/", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        return Results.Redirect("/account/login");
    }

    var body = $"""
        <h1 class="govuk-heading-xl">Wayfinder reference app</h1>
        <p class="govuk-body">A completely transient, in-memory host demonstrating Wayfinder's engine, authoring
        API/MCP and editor — seeded with GOV.UK Service Manual's own "Apply for a licence to
        hold a juggling event" exemplar, and a second citizen/caseworker demo showcasing
        slider/stat-group/chart.</p>
        <ul class="govuk-list">
          {(ctx.User.IsInRole(DemoUsers.ApplicantRole) ? """<li><a class="govuk-link" href="/apply">Apply for a juggling licence</a> — the applicant's frontstage journey.</li>""" : "")}
          {(ctx.User.IsInRole(DemoUsers.ApplicantRole) ? """<li><a class="govuk-link" href="/premium">Model your performance insurance premium</a> — an interactive slider/stat-group/chart-driven modeller.</li>""" : "")}
          {(ctx.User.IsInRole(DemoUsers.CaseworkerRole) ? """<li><a class="govuk-link" href="/caseworker/queue">Caseworker queue</a> — the backstage review queue, shared across both demos.</li>""" : "")}
          <li><a class="govuk-link" href="/service-blueprint-editor">Service blueprint editor</a> — author/edit either seeded blueprint live.</li>
        </ul>
        """;
    return Results.Content(PageShell.Render("Home", body, ctx.User), "text/html");
});

// ── Auth pages ────────────────────────────────────────────────────────────────────────────

app.MapGet("/account/login", (string? returnUrl, HttpContext ctx) =>
    // Pass ctx.User (not null) so the nav still reflects an already-authenticated visitor who
    // landed here via a failed role check (AccessDeniedPath), not just an anonymous one.
    Results.Content(PageShell.Render("Sign in", RenderLoginBody(returnUrl, null), ctx.User), "text/html"));

app.MapPost("/account/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var demoUser = DemoUsers.Find(email);
    if (demoUser is null || password != DemoUsers.DemoPassword)
    {
        var body = RenderLoginBody(returnUrl, "Enter a valid demo email address and password.");
        return Results.Content(PageShell.Render("Sign in", body, ctx.User), "text/html");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, demoUser.Email),
        new(ClaimTypes.Name, demoUser.DisplayName),
        new(ClaimTypes.Email, demoUser.Email),
        new(ClaimTypes.Role, demoUser.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    var isLocalReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal);
    var landing = isLocalReturnUrl
        ? returnUrl
        : demoUser.Role == DemoUsers.CaseworkerRole ? "/caseworker/queue" : "/apply";
    return Results.Redirect(landing);
});

app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
});

// ── Frontstage: the applicant's own journey ──────────────────────────────────────────────

var citizenGroup = app.MapGroup("/apply").RequireAuthorization("Applicant");

citizenGroup.MapGet("/", (HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer) =>
{
    var envelope = engine.GetCurrent(
        JugglingLicenceDefinitionKey, ReferenceActors.TenantId, GetUserId(ctx.User), ReferenceActors.CitizenProfile());
    return Results.Content(PageShell.Render("Apply for a juggling licence", RenderJourneyBody(envelope, "/apply", renderer), ctx.User), "text/html");
});

citizenGroup.MapPost("/", async (HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer, IServiceRequestFileStorage fileStorage) =>
{
    var userId = GetUserId(ctx.User);
    var profile = ReferenceActors.CitizenProfile();
    var current = engine.GetCurrent(JugglingLicenceDefinitionKey, ReferenceActors.TenantId, userId, profile);

    var form = await ctx.Request.ReadFormAsync();
    var action = form["action"].ToString();
    var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
    var fieldValues = CoerceFieldValues(form, current.Render);

    var fileErrors = await ApplyFileUploadsAsync(form, current.Render, current.InstanceId, fileStorage, fieldValues);
    if (fileErrors.Count > 0)
    {
        return Results.Content(
            PageShell.Render("Apply for a juggling licence", RenderJourneyBody(current with { Problems = fileErrors }, "/apply", renderer), ctx.User), "text/html");
    }

    var result = engine.Advance(current.InstanceId, ReferenceActors.TenantId, userId, profile, action, stateVersion, fieldValues);

    // A rejected submission (missing/invalid field values, or a field key that isn't even
    // declared on this stage — see ProcessManagerEngine.Advance's server-side validation)
    // never changes instance state, so stateVersion is still current — safe to render this
    // response directly rather than redirect, unlike a genuine advance below.
    if (result.Problems.Count > 0 && result.Render is not null)
    {
        return Results.Content(
            PageShell.Render("Apply for a juggling licence", RenderJourneyBody(result, "/apply", renderer), ctx.User), "text/html");
    }

    // Redirect rather than render the result directly (POST-redirect-GET): rendering at the
    // POST URL leaves that response in browser history, so reloading it — or a caseworker
    // advancing the same instance from another tab — resubmits the same stale stateVersion
    // and fails with a spurious VERSION_MISMATCH. Redirecting to the GET route always re-fetches
    // whatever the instance's current state actually is.
    return Results.Redirect("/apply");
});

// A second, independent citizen-queue demo — slider/stat-group/chart-driven interactive
// modelling, ending in a "send to a caseworker" fan-out into the same backstage queue below.
// Same shape as the /apply group above; a distinct route prefix and definition key are the
// only difference, since ActorProfile/queue access is keyed by queue name, not blueprint key.
var premiumGroup = app.MapGroup("/premium").RequireAuthorization("Applicant");

premiumGroup.MapGet("/", (HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer) =>
{
    var envelope = engine.GetCurrent(
        InsuranceModellerDefinitionKey, ReferenceActors.TenantId, GetUserId(ctx.User), ReferenceActors.CitizenProfile());
    return Results.Content(PageShell.Render("Model your performance insurance premium", RenderJourneyBody(envelope, "/premium", renderer), ctx.User), "text/html");
});

premiumGroup.MapPost("/", async (HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer, IServiceRequestFileStorage fileStorage) =>
{
    var userId = GetUserId(ctx.User);
    var profile = ReferenceActors.CitizenProfile();
    var current = engine.GetCurrent(InsuranceModellerDefinitionKey, ReferenceActors.TenantId, userId, profile);

    var form = await ctx.Request.ReadFormAsync();
    var action = form["action"].ToString();
    var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
    var fieldValues = CoerceFieldValues(form, current.Render);

    var fileErrors = await ApplyFileUploadsAsync(form, current.Render, current.InstanceId, fileStorage, fieldValues);
    if (fileErrors.Count > 0)
    {
        return Results.Content(
            PageShell.Render("Model your performance insurance premium", RenderJourneyBody(current with { Problems = fileErrors }, "/premium", renderer), ctx.User), "text/html");
    }

    var result = engine.Advance(current.InstanceId, ReferenceActors.TenantId, userId, profile, action, stateVersion, fieldValues);

    if (result.Problems.Count > 0 && result.Render is not null)
    {
        return Results.Content(
            PageShell.Render("Model your performance insurance premium", RenderJourneyBody(result, "/premium", renderer), ctx.User), "text/html");
    }

    return Results.Redirect("/premium");
});

// ── Backstage: the caseworker's review queue ─────────────────────────────────────────────

var caseworkerGroup = app.MapGroup("/caseworker").RequireAuthorization("Caseworker");

caseworkerGroup.MapGet("/queue", (HttpContext ctx, IProcessManager engine) =>
{
    // Genuinely multi-blueprint: GetQueueWorkItems has no blueprint filter, so both demos'
    // caseworker-queue items already show up here side by side, keyed by queue name alone.
    var items = engine.GetQueueWorkItems(ReferenceActors.CaseworkerProfile()).Items;
    var esc = GovUk.Esc;
    var rows = items.Count == 0
        ? """<tr class="govuk-table__row"><td class="govuk-table__cell" colspan="4">No applications waiting for review</td></tr>"""
        : string.Join("\n", items.Select(item => $"""
            <tr class="govuk-table__row">
              <td class="govuk-table__cell">{esc(item.BlueprintDisplayName)}</td>
              <td class="govuk-table__cell">{esc(item.StateDisplayName)}</td>
              <td class="govuk-table__cell">{esc(item.InstanceId[..Math.Min(8, item.InstanceId.Length)])}…</td>
              <td class="govuk-table__cell"><a class="govuk-link" href="/caseworker/queue/{Uri.EscapeDataString(item.BlueprintKey)}/{Uri.EscapeDataString(item.InstanceId)}">Review</a></td>
            </tr>
            """));

    var body = $"""
        <h1 class="govuk-heading-xl">Caseworker queue</h1>
        <table class="govuk-table">
          <thead class="govuk-table__head">
            <tr class="govuk-table__row">
              <th class="govuk-table__header" scope="col">Service</th>
              <th class="govuk-table__header" scope="col">Stage</th>
              <th class="govuk-table__header" scope="col">Instance</th>
              <th class="govuk-table__header" scope="col"><span class="govuk-visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody class="govuk-table__body">{rows}</tbody>
        </table>
        """;
    return Results.Content(PageShell.Render("Caseworker queue", body, ctx.User), "text/html");
});

caseworkerGroup.MapGet("/queue/{blueprintKey}/{instanceId}", (string blueprintKey, string instanceId, HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer) =>
{
    var envelope = engine.GetCurrent(
        blueprintKey, ReferenceActors.TenantId, GetUserId(ctx.User), ReferenceActors.CaseworkerProfile(), instanceId);
    var downloadLinks = BuildFileDownloadLinks(
        engine, instanceId, envelope.Render, $"/caseworker/queue/{blueprintKey}/{instanceId}/files");
    return Results.Content(
        PageShell.Render(
            "Review application",
            RenderJourneyBody(envelope, $"/caseworker/queue/{blueprintKey}/{instanceId}/advance", renderer) + downloadLinks,
            ctx.User),
        "text/html");
});

caseworkerGroup.MapGet("/queue/{blueprintKey}/{instanceId}/files/{fieldKey}", async (
    string blueprintKey, string instanceId, string fieldKey, IProcessManager engine, IServiceRequestFileStorage fileStorage) =>
{
    var rawValues = engine.GetAllInstances().FirstOrDefault(request => request.InstanceId == instanceId)?.FieldValues;
    var reference = rawValues is null ? null : ServiceRequestFileReference.FromFieldValue(rawValues.GetValueOrDefault(fieldKey));
    if (reference is null)
    {
        return Results.NotFound();
    }

    var stream = await fileStorage.OpenReadAsync(reference.StorageKey);
    if (stream is null)
    {
        return Results.NotFound();
    }

    var contentType = string.IsNullOrEmpty(reference.ContentType) ? "application/octet-stream" : reference.ContentType;
    return Results.File(stream, contentType, reference.OriginalFileName);
});

caseworkerGroup.MapPost("/queue/{blueprintKey}/{instanceId}/advance", async (string blueprintKey, string instanceId, HttpContext ctx, IProcessManager engine, GovUkComponentRenderer renderer, IServiceRequestFileStorage fileStorage) =>
{
    var userId = GetUserId(ctx.User);
    var profile = ReferenceActors.CaseworkerProfile();
    var current = engine.GetCurrent(blueprintKey, ReferenceActors.TenantId, userId, profile, instanceId);

    var form = await ctx.Request.ReadFormAsync();
    var action = form["action"].ToString();
    var stateVersion = int.TryParse(form["stateVersion"], out var version) ? version : current.StateVersion;
    var fieldValues = CoerceFieldValues(form, current.Render);

    var fileErrors = await ApplyFileUploadsAsync(form, current.Render, instanceId, fileStorage, fieldValues);
    if (fileErrors.Count > 0)
    {
        return Results.Content(
            PageShell.Render("Review application", RenderJourneyBody(current with { Problems = fileErrors }, $"/caseworker/queue/{blueprintKey}/{instanceId}/advance", renderer), ctx.User), "text/html");
    }

    var result = engine.Advance(instanceId, ReferenceActors.TenantId, userId, profile, action, stateVersion, fieldValues);

    if (result.Problems.Count > 0 && result.Render is not null)
    {
        return Results.Content(
            PageShell.Render("Review application", RenderJourneyBody(result, $"/caseworker/queue/{blueprintKey}/{instanceId}/advance", renderer), ctx.User), "text/html");
    }

    return Results.Redirect("/caseworker/queue");
});

// ── Test isolation ────────────────────────────────────────────────────────────────────────
// Development-only: wipes every in-memory instance and authoring override, so a Playwright
// spec starts each test from the same seeded-but-empty state instead of restarting the process.
app.MapDelete("/api/test/reset", (IProcessManager engine, InMemoryRuntimeServiceBlueprintSourceStore sourceStore, IHostEnvironment env) =>
{
    if (!env.IsDevelopment())
    {
        return Results.NotFound();
    }

    engine.ResetAll();
    sourceStore.ClearOverrides();
    return Results.Ok(new { cleared = true });
});

app.Run();

static string GetUserId(ClaimsPrincipal user) =>
    user.FindFirst(ClaimTypes.NameIdentifier)?.Value
    ?? throw new InvalidOperationException("Authenticated request has no NameIdentifier claim.");

static string RenderJourneyBody(ServiceRequestResponseEnvelope envelope, string formAction, GovUkComponentRenderer renderer)
{
    var esc = GovUk.Esc;
    if (envelope.Render is null)
    {
        var message = envelope.Problems.FirstOrDefault()?.Message ?? "Nothing to show.";
        return $"""<div id="wayfinder-journey"><p class="govuk-body">{esc(message)}</p></div>""";
    }

    // A panel component (confirmation/outcome stages) already renders its own <h1> — a second
    // page heading here would be a duplicate, which the real GOV.UK panel component isn't
    // designed to sit under.
    var heading = GovUkComponentRenderer.HasPanel(envelope.Render)
        ? ""
        : $"""<h1 class="govuk-heading-xl">{esc(envelope.Render.StateDisplayName)}</h1>""";

    // The fixed id is what /js/wayfinder-live-recalculate.js swaps in place after an AJAX
    // "recalculate" submit — any stage offering that action (a slider/radio-driven interactive
    // island, per Wayfinder.Rendering.GovUk's slider/stat-group/chart markup) gets a genuinely
    // live-feeling result with zero duplication of the calculation logic itself, which stays
    // server-side and single-sourced from the blueprint's own calculations block.
    return $"""<div id="wayfinder-journey">{heading}{renderer.RenderForm(envelope.Render, envelope.Problems, formAction, envelope.StateVersion)}</div>""";
}

/// <summary>
/// A caseworker reviewing an application needs to actually open what was uploaded, not just see
/// its filename — this is the demo's one exercise of IServiceRequestFileStorage.OpenReadAsync,
/// the read half of the interface the rest of the app only ever writes to. Generic over every
/// file-upload field the current stage renders, so it needs no per-blueprint wiring: any new
/// file-upload field anywhere just starts working here too. Reads the *raw* persisted
/// FieldValues (not the rendered display value, which is deliberately just a filename — see
/// ProcessManagerEngine.GetDisplayValue) because only the raw ServiceRequestFileReference still
/// carries the storage key OpenReadAsync needs.
/// </summary>
static string BuildFileDownloadLinks(IProcessManager engine, string instanceId, StepContent? render, string downloadUrlPrefix)
{
    if (render is null)
    {
        return "";
    }

    var fileFields = render.Components
        .SelectMany(component => component.Fields)
        .Where(field => field.FieldType == "file-upload")
        .ToList();
    if (fileFields.Count == 0)
    {
        return "";
    }

    var rawValues = engine.GetAllInstances().FirstOrDefault(request => request.InstanceId == instanceId)?.FieldValues;
    if (rawValues is null)
    {
        return "";
    }

    var esc = GovUk.Esc;
    var links = fileFields
        .Select(field => (Field: field, Reference: ServiceRequestFileReference.FromFieldValue(rawValues.GetValueOrDefault(field.FieldKey))))
        .Where(entry => entry.Reference is not null)
        .Select(entry => $"""
            <li><a class="govuk-link" href="{downloadUrlPrefix}/{Uri.EscapeDataString(entry.Field.FieldKey)}">{esc(entry.Field.Label)} ({esc(entry.Reference!.OriginalFileName)})</a></li>
            """)
        .ToList();

    return links.Count == 0
        ? ""
        : $"""
            <h2 class="govuk-heading-m">Uploaded files</h2>
            <ul class="govuk-list">{string.Join("\n", links)}</ul>
            """;
}

static string RenderLoginBody(string? returnUrl, string? error)
{
    var esc = GovUk.Esc;
    var errorHtml = error is null
        ? ""
        : $"""
            <div class="govuk-error-summary" data-module="govuk-error-summary">
              <div role="alert">
                <h2 class="govuk-error-summary__title">There is a problem</h2>
                <div class="govuk-error-summary__body">
                  <ul class="govuk-list govuk-error-summary__list">
                    <li><a href="#email">{esc(error)}</a></li>
                  </ul>
                </div>
              </div>
            </div>
            """;

    // The real GOV.UK password-input component (govuk-frontend/dist/govuk/components/password-input) —
    // requires govuk-frontend.min.js's initAll() (see PageShell.cs) for the show/hide toggle.
    return $"""
        <h1 class="govuk-heading-xl">Sign in</h1>
        {errorHtml}
        <form method="post" action="/account/login">
          <input type="hidden" name="returnUrl" value="{esc(returnUrl ?? "")}" />
          <div class="govuk-form-group">
            <label class="govuk-label" for="email">Email address</label>
            <input class="govuk-input" id="email" name="email" type="email" autocomplete="email" spellcheck="false" required>
          </div>
          <div class="govuk-form-group govuk-password-input" data-module="govuk-password-input">
            <label class="govuk-label" for="password">Password</label>
            <div class="govuk-input__wrapper govuk-password-input__wrapper">
              <input class="govuk-input govuk-password-input__input govuk-js-password-input-input" id="password" name="password" type="password" spellcheck="false" autocomplete="current-password" autocapitalize="none" required>
              <button type="button" class="govuk-button govuk-button--secondary govuk-password-input__toggle govuk-js-password-input-toggle" data-module="govuk-button" aria-controls="password" aria-label="Show password" hidden>Show</button>
            </div>
          </div>
          <div class="govuk-button-group">
            <button class="govuk-button" data-module="govuk-button" type="submit">Sign in</button>
          </div>
        </form>
        <h2 class="govuk-heading-m">Demo accounts</h2>
        <p class="govuk-body">Both demo accounts use the password <code class="govuk-!-font-family-sans-serif">{esc(DemoUsers.DemoPassword)}</code>.</p>
        <ul class="govuk-list govuk-list--bullet">
          <li><strong>{esc(DemoUsers.Applicant.DisplayName)}</strong> — {esc(DemoUsers.Applicant.Email)} (applicant / frontstage)</li>
          <li><strong>{esc(DemoUsers.Caseworker.DisplayName)}</strong> — {esc(DemoUsers.Caseworker.Email)} (caseworker / backstage)</li>
        </ul>
        """;
}

/// <summary>
/// Reads posted <c>field:{fieldKey}</c> values back into the CLR shapes the engine expects,
/// using the field-type map from the stage that produced the form (a checkbox posts nothing at
/// all when unchecked, so boolean fields need explicit false; number/decimal fields parse to a
/// real number rather than staying a string).
/// </summary>
static Dictionary<string, object?> CoerceFieldValues(IFormCollection form, StepContent? render)
{
    var fieldValues = new Dictionary<string, object?>();
    if (render is null)
    {
        return fieldValues;
    }

    var fieldsByKey = render.Components
        .SelectMany(component => component.Fields)
        .ToDictionary(field => field.FieldKey, field => field.FieldType, StringComparer.Ordinal);

    foreach (var (fieldKey, fieldType) in fieldsByKey)
    {
        var formKey = $"field:{fieldKey}";

        if (fieldType == "boolean")
        {
            fieldValues[fieldKey] = form.ContainsKey(formKey);
            continue;
        }

        // The real GOV.UK date-input component posts three separate day/month/year fields
        // rather than one native date value — see Wayfinder.Rendering.GovUk.GovUkFields' date field.
        if (fieldType == "date")
        {
            var combined = GovUk.CombineIsoDate(
                form[$"{formKey}-day"], form[$"{formKey}-month"], form[$"{formKey}-year"]);
            if (combined is not null)
            {
                fieldValues[fieldKey] = combined;
            }
            continue;
        }

        if (!form.TryGetValue(formKey, out var raw))
        {
            continue;
        }

        fieldValues[fieldKey] = fieldType switch
        {
            "number" or "decimal" => decimal.TryParse(raw, out var number) ? number : raw.ToString(),
            _ => raw.ToString()
        };
    }

    return fieldValues;
}

/// <summary>
/// Handles every <c>file-upload</c> field on the current stage: validates a posted file against
/// its own declared <c>MaxSizeBytes</c>/<c>AcceptedFileTypes</c> — server-side, since the engine
/// itself never sees bytes and can't be the enforcement point (see
/// Wayfinder.Engine.Abstractions.IServiceRequestFileStorage) — then saves it and writes the
/// resulting reference into <paramref name="fieldValues"/>. A field with no file posted this
/// time round is left untouched entirely, so the engine's own merge preserves whatever
/// reference (if any) the instance already has stored, the same as any other unchanged field.
/// Returns one <see cref="ServiceRequestProblem"/> per rejected file; empty means every posted
/// file was accepted (or none were posted at all).
/// </summary>
static async Task<List<ServiceRequestProblem>> ApplyFileUploadsAsync(
    IFormCollection form, StepContent? render, string instanceId, IServiceRequestFileStorage fileStorage, Dictionary<string, object?> fieldValues)
{
    const long defaultMaxSizeBytes = 10 * 1024 * 1024;
    var problems = new List<ServiceRequestProblem>();
    if (render is null)
    {
        return problems;
    }

    var fileUploadFields = render.Components
        .SelectMany(component => component.Fields)
        .Where(field => field.FieldType == "file-upload");

    foreach (var field in fileUploadFields)
    {
        var formKey = $"field:{field.FieldKey}";
        var file = form.Files.GetFile(formKey);
        if (file is null || file.Length == 0)
        {
            continue; // Nothing new posted — leave the instance's existing reference (if any) untouched.
        }

        var maxSizeBytes = field.MaxSizeBytes ?? defaultMaxSizeBytes;
        if (file.Length > maxSizeBytes)
        {
            problems.Add(new ServiceRequestProblem
            {
                FieldKey = field.FieldKey,
                Message = $"{field.Label} must be smaller than {maxSizeBytes / (1024 * 1024)}MB.",
                Code = "VALIDATION_ERROR"
            });
            continue;
        }

        var extension = Path.GetExtension(file.FileName);
        if (field.AcceptedFileTypes is { Count: > 0 } accepted
            && !accepted.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new ServiceRequestProblem
            {
                FieldKey = field.FieldKey,
                Message = $"{field.Label} must be one of: {string.Join(", ", accepted)}.",
                Code = "VALIDATION_ERROR"
            });
            continue;
        }

        await using var stream = file.OpenReadStream();
        var storageKey = await fileStorage.SaveAsync(instanceId, field.FieldKey, stream, file.FileName);
        // The engine's own GetDisplayValue (ProcessManagerEngine) only recognises a file-upload
        // field's persisted value as a ServiceRequestFileReference (or its JsonElement round
        // trip) — a bare storage-key string displays as nothing at all, which also leaves the
        // rendered <input> incorrectly marked required after a validation bounce-back, since a
        // browser can't pre-populate a file input from a prior selection.
        fieldValues[field.FieldKey] = new ServiceRequestFileReference
        {
            StorageKey = storageKey,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            SizeBytes = file.Length,
        };
    }

    return problems;
}

/// <summary>
/// Re-roots an <see cref="IFileProvider"/> at a fixed subpath — serves Wayfinder.Editor's
/// static web assets (found under its default "_content/Wayfinder.Editor/..." prefix inside
/// this app's composite WebRootFileProvider) at web root instead, without hardcoding any
/// machine-specific NuGet/build-output path.
/// </summary>
file sealed class SubPathFileProvider(IFileProvider inner, string subpath) : IFileProvider
{
    private string Rebase(string path) => $"{subpath}/{path.TrimStart('/')}";

    public IFileInfo GetFileInfo(string subpath_) => inner.GetFileInfo(Rebase(subpath_));

    public IDirectoryContents GetDirectoryContents(string subpath_) => inner.GetDirectoryContents(Rebase(subpath_));

    public IChangeToken Watch(string filter) => inner.Watch(Rebase(filter));
}
