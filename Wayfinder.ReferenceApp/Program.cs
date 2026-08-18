using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wayfinder.Editor;
using Wayfinder.Editor.Http;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Api;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Http;
using Wayfinder.Engine.Journey;
using Wayfinder.Engine.Mcp;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Engine.Worklist;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.ReferenceApp.Services;
using Wayfinder.ReferenceApp.Services.SupportSystems;
using Wayfinder.Rendering.GovUk;
using Wayfinder.Services.Sanitization;

// The seeded demo blueprints' keys — see service-blueprints/juggling-licence.json and
// service-blueprints/juggling-insurance-modeller.json.
const string JugglingLicenceDefinitionKey = "juggling-licence";
const string InsuranceModellerDefinitionKey = "juggling-insurance-modeller";
const string NjfContributionsDefinitionKey = "njf-contributions";

// Must run before anything reads ComponentTypeRegistry (it freezes on first read) — the seed
// blueprints below declare a "rating" component (juggling-licence.json's "event-details" stage),
// proving a genuinely new, host-defined component type registered from outside Wayfinder's own
// assembly. See Services/CustomComponents.cs and docs/guides/extending-the-component-catalog.md.
CustomComponents.Register();

// Same "freezes on first read" reasoning as ComponentTypeRegistry above, for
// SupportSystemRegistry — see Services/SupportSystems/SafetyNetUnderwritingClient.cs and
// docs/guides/support-systems.md.
SafetyNetUnderwriting.Register();

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
builder.Services.AddSingleton<IServiceContentSanitizer, PassthroughContentSanitizer>();
builder.Services.AddSingleton<IServiceBlueprintStore>(
    _ => new FilesystemServiceBlueprintStore(Path.Combine(builder.Environment.ContentRootPath, "service-blueprints")));
builder.Services.AddSingleton<IQueueCapabilitiesProvider>(ReferenceActors.CapabilitiesProvider());
builder.Services.AddSingleton<IServiceRequestFileStorage, InMemoryServiceRequestFileStorage>();
builder.Services.AddSingleton<IBulkDatasetStore>(
    sp => new InMemoryBulkDatasetStore(sp.GetRequiredService<IServiceRequestFileStorage>()));

// SafetyNet Underwriting is a genuinely separate ASP.NET Core project (see
// SafetyNetUnderwriting/Program.cs), orchestrated alongside this one by Wayfinder.AppHost — the
// base address here is its Aspire resource name, resolved by the AddServiceDiscovery() handler
// AddServiceDefaults() already wired above. "http://referenceapp" (this app's own resource name)
// is what SafetyNetUnderwritingClient tells SafetyNet Underwriting to call back on.
builder.Services.AddHttpClient(SafetyNetUnderwriting.HttpClientName, client =>
{
    client.BaseAddress = new Uri("http://safetynet-underwriting");
});

// Wayfinder.Rendering.GovUk's built-in catalog covers every built-in component/field type out
// of the box. This reference app registers exactly one override — CustomComponents.RegisterRendering
// pairs the "rating" type registered above with real govuk-frontend-styled HTML, its own
// GovUkComponentRenderer.RegisterField call, no Razor/ViewEngine ceremony required.
builder.Services.AddSingleton(_ =>
{
    var renderer = new GovUkComponentRenderer();
    CustomComponents.RegisterRendering(renderer);
    return renderer;
});

builder.Services.AddSingleton(sp => new ProcessManagerEngine(
    sp.GetRequiredService<ILogger<ProcessManagerEngine>>(),
    sp.GetRequiredService<IServiceBlueprintStore>(),
    sp.GetRequiredService<IServiceContentSanitizer>(),
    // A "source: service" calculations field (see njf-contributions.json's own contributionsErrorCount
    // — needed so its review stage's "Accept and finish" route's showWhen can see a value that's
    // never a captured input, only an onEnter action's own output) is resolved via this callback,
    // never automatically from FieldValues — CalculationScopeBuilder.Build only pulls genuine
    // captured-input components in on its own. Generic: reads whatever's declared source:"service"
    // straight off the instance's own already-populated FieldValues, since the "service" that
    // supplied it here is Wayfinder's own engine (an action's resolution), not a true external
    // lookup a host would need to actually go and fetch.
    serviceInputsResolver: (instance, definition, _) =>
        (definition.Calculations?.Fields ?? new Dictionary<string, Wayfinder.Models.ServiceDesign.Calculations.ServiceBlueprintCalculationField>())
            .Where(field => string.Equals(field.Value.Source, "service", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(field => field.Key, field => instance.FieldValues.GetValueOrDefault(field.Key)),
    supportSystemClients:
    [
        new SafetyNetUnderwritingClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IServiceRequestFileStorage>(),
            callbackBaseUrl: "http://referenceapp")
    ],
    bulkDatasetStore: sp.GetRequiredService<IBulkDatasetStore>()));
builder.Services.AddSingleton<IProcessManager>(sp => sp.GetRequiredService<ProcessManagerEngine>());

// The editor / REST / MCP authoring surface and the `/apply` + `/caseworker` request-processing
// surface share this one store, so a save from any authoring surface is immediately live for
// the next citizen/caseworker request — see InMemoryRuntimeServiceBlueprintSourceStore's remarks.
builder.Services.AddSingleton<InMemoryRuntimeServiceBlueprintSourceStore>();
builder.Services.AddSingleton<IServiceBlueprintSourceStore>(sp => sp.GetRequiredService<InMemoryRuntimeServiceBlueprintSourceStore>());
builder.Services.AddServiceBlueprintAuthoring();
builder.Services.AddServiceBlueprintAuthoringApi();
builder.Services.AddServiceBlueprintAuthoringMcp();

// See Wayfinder.Engine.Journey's own README — the default single-actor journey surface (get
// current stage, advance it) shared by /apply and /premium below.
builder.Services.AddJourney(options =>
{
    options.ResolveTenantId = _ => ReferenceActors.TenantId;
    options.ResolveAccessProfile = _ => ReferenceActors.CitizenProfile();
    options.RenderPage = (title, body, ctx) => PageShell.Render(title, body, ctx.User);
});

// See Wayfinder.Engine.Worklist's own README — the default caseworker worklist surface
// (list/item/advance/claim/release), covering everything about /caseworker/queue that isn't
// specific to this reference app.
builder.Services.AddWorklist(options =>
{
    options.ResolveTenantId = _ => ReferenceActors.TenantId;
    options.ResolveAccessProfile = ctx => ReferenceActors.ProfileForCaseworkerUser(GetUserId(ctx.User));
    options.RenderPage = (title, body, ctx) => PageShell.Render(title, body, ctx.User);
    options.WorklistPageTitle = "Caseworker queue";
    options.ReviewPageTitle = "Review application";
});

var app = builder.Build();

app.MapDefaultEndpoints();

// This app's own wwwroot — just its favicon/manifest branding now. The real govuk-frontend
// package (CSS/JS/fonts) is vendored inside Wayfinder.Rendering.GovUk instead, version-locked to
// whatever that package's own generated markup actually targets — see PageShell.cs.
app.UseStaticFiles();

// Wayfinder.Editor's own compiled bundle and Wayfinder.Rendering.GovUk's own vendored
// govuk-frontend assets, each served by the package that owns them — see their own
// UseWayfinderEditorAssets()/UseGovUkFrontendAssets() extensions. Distinct sub-path from this
// app's own "/assets/images/..." favicon set above — static-files middleware calls just fall
// through to the next on a miss, so there's no collision.
app.UseWayfinderEditorAssets();
app.UseGovUkFrontendAssets();

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

// The webhook half of support-system outcome delivery — a host's own job, not something
// Wayfinder.Engine.Api ships (that surface is scoped to blueprint authoring only, not runtime
// request handling — see docs/guides/support-systems.md § Delivering the outcome). invocationId
// is itself the unguessable correlation/auth token; ResolveSupportSystemOutcome is the same
// method the engine's own poll-check path calls, so "what did the external system decide" is
// resolved identically regardless of which mechanism delivered it.
app.MapPost("/wayfinder/support-systems/callbacks/{invocationId}", async (
    string invocationId, HttpContext ctx, ProcessManagerEngine engine, CancellationToken ct) =>
{
    var payload = await ctx.Request.ReadFromJsonAsync<JsonObject>(ct);
    var outcomeKey = payload?["outcomeKey"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(outcomeKey))
    {
        return Results.BadRequest("outcomeKey is required.");
    }

    var resultPayload = payload?["resultPayload"] as JsonObject;
    var result = engine.ResolveSupportSystemOutcome(invocationId, outcomeKey, resultPayload);
    return result.ResponseState == "error" ? Results.BadRequest(result) : Results.Ok(result);
});

// See Wayfinder.Editor.Http's own README — the backend half of the contract Wayfinder.Editor's
// packaged demo page (service-blueprint-editor.html) expects from its bundled
// MockBusinessAppServiceBlueprintSource example, rather than the
// `/wayfinder/service-blueprint-authoring/*` routes above. Anonymous, same reasoning as the
// authoring API/MCP above.
app.MapMockBusinessAppServiceBlueprints();

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
        hold a juggling event" exemplar, a second citizen/caseworker demo showcasing
        slider/stat-group/chart, and a caseworker-only demo showcasing bulk data review.</p>
        <ul class="govuk-list">
          {(ctx.User.IsInRole(DemoUsers.ApplicantRole) ? """<li><a class="govuk-link" href="/apply">Apply for a juggling licence</a> — the applicant's frontstage journey.</li>""" : "")}
          {(ctx.User.IsInRole(DemoUsers.ApplicantRole) ? """<li><a class="govuk-link" href="/premium">Model your performance insurance premium</a> — an interactive slider/stat-group/chart-driven modeller.</li>""" : "")}
          {(ctx.User.IsInRole(DemoUsers.CaseworkerRole) ? """<li><a class="govuk-link" href="/caseworker/queue">Caseworker queue</a> — the backstage review queue, shared across all three demos.</li>""" : "")}
          {(ctx.User.IsInRole(DemoUsers.CaseworkerRole) ? """<li><a class="govuk-link" href="/caseworker/njf-contributions/new">Submit an NJF contributions file</a> — bulk data review: only the rows needing attention, corrected and resubmitted without leaving the page.</li>""" : "")}
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

// ── Frontstage: the applicant's own journeys ─────────────────────────────────────────────
// See Wayfinder.Engine.Journey's own README. Two independent self-service demos sharing one
// AddJourney() registration above — a second, independent citizen-queue demo (slider/stat-group/
// chart-driven interactive modelling) fans out into the same backstage queue below as the first,
// since ActorProfile/queue access is keyed by queue name, not blueprint key.
app.MapJourney("/apply", JugglingLicenceDefinitionKey, "Apply for a juggling licence").RequireAuthorization("Applicant");
app.MapJourney("/premium", InsuranceModellerDefinitionKey, "Model your performance insurance premium").RequireAuthorization("Applicant");

// ── Backstage: the caseworker's review queue ─────────────────────────────────────────────

var caseworkerGroup = app.MapGroup("/caseworker").RequireAuthorization("Caseworker");

// The default worklist surface (list/item/advance/claim/release/file-download) plus bulk-data-review's
// own REST endpoints — see Wayfinder.Engine.Worklist's own README. Everything else under
// /caseworker (just the NJF "start new" entry point below) stays hand-wired here.
app.MapWorklist(prefix: "/caseworker/queue").RequireAuthorization("Caseworker");
app.MapBulkDatasetReview(prefix: "/caseworker/queue").RequireAuthorization("Caseworker");

// njf-contributions has no citizen frontstage to originate an instance from (see
// docs/guides/bulk-data-review.md — the NJF's own operations staff are the only actor), so it
// needs its own "start" entry point the way /apply and /premium give the citizen-facing demos —
// A distinct "start a new one" affordance, not "continue where I left off" — GetCurrentOrStartFresh
// reinstates a still-running submission (never abandons in-progress work), but genuinely starts a
// fresh one once the existing one has reached "Contributions file accepted", rather than returning
// that stale confirmation forever the way plain ambient GetCurrent would. The terminal instance
// stays fully reachable either way — via the caseworker queue list's own "Done" status filter (see
// docs/guides/queue-worklist-filtering.md), which is what this route's own visibility gap led to —
// so nothing is lost by not returning it here.
//
// NjfOperationsProfile's own ConcurrencyScopeKey means this also already enforces "only one bulk
// load per juggling authority" — every NJF operations user shares that scope, so this finds (and
// reinstates, or replaces once terminal) the same instance regardless of which of them started it.
caseworkerGroup.MapGet("/njf-contributions/new", (HttpContext ctx, IProcessManager engine) =>
{
    var started = engine.GetCurrentOrStartFresh(
        NjfContributionsDefinitionKey, ReferenceActors.TenantId, GetUserId(ctx.User), ReferenceActors.NjfOperationsProfile());
    return Results.Redirect($"/caseworker/queue/{NjfContributionsDefinitionKey}/{started.InstanceId}");
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
        <p class="govuk-body">All demo accounts use the password <code class="govuk-!-font-family-sans-serif">{esc(DemoUsers.DemoPassword)}</code>.</p>
        <ul class="govuk-list govuk-list--bullet">
          <li><strong>{esc(DemoUsers.Applicant.DisplayName)}</strong> — {esc(DemoUsers.Applicant.Email)} (applicant / frontstage)</li>
          <li><strong>{esc(DemoUsers.Caseworker.DisplayName)}</strong> — {esc(DemoUsers.Caseworker.Email)} (caseworker / backstage, juggling licences)</li>
          <li><strong>{esc(DemoUsers.NjfOperations.DisplayName)}</strong> — {esc(DemoUsers.NjfOperations.Email)} (caseworker / backstage, NJF contributions)</li>
        </ul>
        """;
}
