using System.Security.Claims;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// Shared HTML page chrome for every hand-rolled page in this reference app — the real GOV.UK
/// Design System page skeleton, copied from <c>govuk-frontend</c>'s own
/// <c>dist/govuk/template.njk</c> (head/favicon block, skip link, header/footer landmark
/// structure, the <c>js-enabled</c> progressive-enhancement script) rather than approximated.
/// Uses the <c>generic-header</c> component (no crown) since this isn't a government service,
/// and a minimal real <c>govuk-footer</c> with this repo's own MIT licence instead of the
/// Crown-copyright/OGL text those government-specific footer examples carry. Same reasoning for
/// the favicon/touch-icon set: template.njk's own assets are the GOV.UK crest, replaced here
/// with a compass mark (Bootstrap Icons' own <c>compass-fill</c>, MIT licensed, vendored the same
/// way as govuk-frontend — see wwwroot/assets/images/favicon.svg) and reused as the small inline
/// SVGs throughout <see cref="RenderServiceNavigation"/> and the home page's own link list.
/// </summary>
public static class PageShell
{
    // A plain (non-interpolated) raw string — JS import braces would otherwise collide with
    // interpolated raw string literals' own interpolation-hole syntax. Exact usage from
    // govuk-frontend's own README "Importing JavaScript" quick-start.
    private const string InitScript = """
        import { initAll } from '/govuk-frontend/govuk-frontend.min.js'
        initAll()
        """;

    // A join gateway's waiting stage (RenderWaiting in GovUkComponents.cs) carries its own
    // authored poll interval as data-wayfinder-poll-interval-ms. This host is hand-rolled,
    // server-rendered HTML with no client-side router, so "poll" here just means reload the
    // page after that interval — the server re-evaluates the request's cursor state on every
    // request, so a still-waiting applicant gets the same page back (with a fresh timer) and
    // one whose case has moved on gets the next stage automatically, with no manual refresh.
    private const string PollScript = """
        var pollTarget = document.querySelector('[data-wayfinder-poll-interval-ms]');
        if (pollTarget) {
          var intervalMs = Number(pollTarget.getAttribute('data-wayfinder-poll-interval-ms'));
          if (intervalMs > 0) {
            setTimeout(function () { location.reload(); }, intervalMs);
          }
        }
        """;

    public static string Render(string title, string bodyHtml, ClaimsPrincipal? user)
    {
        var esc = GovUk.Esc;
        var nav = user?.Identity?.IsAuthenticated == true ? RenderServiceNavigation(user) : "";

        return $"""
            <!DOCTYPE html>
            <html lang="en" class="govuk-template">
            <head>
              <meta charset="utf-8">
              <title>{esc(title)} — Wayfinder reference app</title>
              <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
              <meta name="theme-color" content="#1d70b8">
              <link rel="stylesheet" href="/govuk-frontend/govuk-frontend.min.css">
              <link rel="stylesheet" href="/css/wayfinder-components.css">
              <link rel="icon" sizes="48x48" href="/assets/images/favicon.ico">
              <link rel="icon" sizes="any" href="/assets/images/favicon.svg" type="image/svg+xml">
              <link rel="mask-icon" href="/assets/images/wayfinder-icon-mask.svg" color="#1d70b8">
              <link rel="apple-touch-icon" href="/assets/images/wayfinder-icon-180.png">
              <link rel="manifest" href="/assets/manifest.json">
            </head>
            <body class="govuk-template__body">
              <script>document.body.className += ' js-enabled' + ('noModule' in HTMLScriptElement.prototype ? ' govuk-frontend-supported' : '');</script>
              <a href="#main-content" class="govuk-skip-link" data-module="govuk-skip-link">Skip to main content</a>

              <header class="govuk-template__header">
                <div class="govuk-generic-header">
                  <div class="govuk-generic-header__container govuk-width-container">
                    <div class="govuk-generic-header__logo">
                      <a href="/" class="govuk-generic-header__homepage-link wayfinder-brand-link">{WayfinderIcons.Compass}<span>Wayfinder Reference App</span></a>
                    </div>
                  </div>
                </div>
                {nav}
              </header>

              <div class="govuk-width-container">
                <main class="govuk-main-wrapper" id="main-content">
                  {bodyHtml}
                </main>
              </div>

              <footer class="govuk-template__footer">
                <div class="govuk-footer">
                  <div class="govuk-width-container">
                    <div class="govuk-footer__meta">
                      <div class="govuk-footer__meta-item govuk-footer__meta-item--grow">
                        <h2 class="govuk-visually-hidden">Support links</h2>
                        <div class="govuk-footer__meta-custom">
                          A completely transient, in-memory Wayfinder reference host — <a class="govuk-footer__link" href="https://github.com/jonnymuir/Wayfinder">github.com/jonnymuir/Wayfinder</a>, MIT licensed.
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </footer>

              <script type="module" src="/govuk-frontend/govuk-frontend.min.js"></script>
              <script type="module">{InitScript}</script>
              <script>{PollScript}</script>
              <script src="/js/wayfinder-slider.js"></script>
              <script src="/js/wayfinder-live-recalculate.js"></script>
            </body>
            </html>
            """;
    }

    private static string RenderServiceNavigation(ClaimsPrincipal user)
    {
        var esc = GovUk.Esc;
        var items = new List<string>();

        if (user.IsInRole(DemoUsers.ApplicantRole))
        {
            items.Add($"""<li class="govuk-service-navigation__item"><a class="govuk-service-navigation__link wayfinder-nav-link" href="/apply">{WayfinderIcons.PencilSquare}Apply</a></li>""");
            items.Add($"""<li class="govuk-service-navigation__item"><a class="govuk-service-navigation__link wayfinder-nav-link" href="/premium">{WayfinderIcons.Sliders}Model premium</a></li>""");
        }

        if (user.IsInRole(DemoUsers.CaseworkerRole))
        {
            items.Add($"""<li class="govuk-service-navigation__item"><a class="govuk-service-navigation__link wayfinder-nav-link" href="/caseworker/queue">{WayfinderIcons.Inbox}Caseworker queue</a></li>""");
        }

        items.Add($"""<li class="govuk-service-navigation__item"><a class="govuk-service-navigation__link wayfinder-nav-link" href="/service-blueprint-editor">{WayfinderIcons.DiagramThree}Editor</a></li>""");
        items.Add($"""
            <li class="govuk-service-navigation__item">
              <span class="govuk-service-navigation__link wayfinder-nav-link" style="cursor:default">{WayfinderIcons.PersonCircle}Signed in as {esc(user.Identity!.Name)} ({esc(user.FindFirst(ClaimTypes.Role)?.Value)})</span>
            </li>
            """);
        items.Add($"""
            <li class="govuk-service-navigation__item">
              <form method="post" action="/account/logout">
                <button class="govuk-service-navigation__link govuk-button--text-as-link wayfinder-nav-link" type="submit" style="background:none;border:0;padding:0;font:inherit;cursor:pointer">{WayfinderIcons.BoxArrowRight}Sign out</button>
              </form>
            </li>
            """);

        return $"""
            <div class="govuk-service-navigation" data-module="govuk-service-navigation">
              <div class="govuk-width-container">
                <div class="govuk-service-navigation__container">
                  <nav aria-label="Menu" class="govuk-service-navigation__wrapper">
                    <button type="button" class="govuk-service-navigation__toggle govuk-js-service-navigation-toggle" aria-controls="navigation" hidden aria-hidden="true">Menu</button>
                    <ul class="govuk-service-navigation__list" id="navigation">
                      {string.Join("\n", items)}
                    </ul>
                  </nav>
                </div>
              </div>
            </div>
            """;
    }
}
