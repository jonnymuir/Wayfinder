// The waiting stage's poll behaviour, for every host — one script, no host-specific code and
// no routes baked in. Keyed entirely off data-* attributes on the waiting element (RenderWaiting
// in GovUkComponents.cs, or a host's own equivalent markup). Shipped as this package's own
// static web asset (/_content/Wayfinder.Rendering.GovUk/js/wayfinder-poll.js).
//
// Mode A — the element carries data-wayfinder-poll-interval-ms only:
//   the plain-HTML default. Reload the whole page after that interval. The server re-evaluates
//   the request's cursor state on every request, so a still-waiting applicant gets the same page
//   back (with a fresh timer) and one whose case has moved on gets the next stage automatically,
//   with no manual refresh. The interval is the join gateway's own authored value — Wayfinder
//   behaviour, not a host decision, which is why honouring it lives here and not in host code.
//
// Mode B — the element also carries data-wayfinder-poll-url:
//   poll that URL instead of blind-reloading. The host owns the URL entirely — its own endpoint,
//   its own query string, including whatever "state version the client already knows" token it
//   needs baked in. This script only GETs it and reloads the page when the JSON response has
//   "changed": true. A host with its own SPA router would re-fetch and re-render in place
//   instead; this remains the plain-HTML-host default.
//   Optional companions:
//     data-wayfinder-poll-status-id  — id of an aria-live element to narrate progress into
//     data-wayfinder-poll-max-retries — cap on poll attempts before asking for a manual refresh
//                                       (default 100)
(function () {
  var el = document.querySelector('[data-wayfinder-poll-url], [data-wayfinder-poll-interval-ms]');
  if (!el) {
    return;
  }

  var intervalMs = Number(el.getAttribute('data-wayfinder-poll-interval-ms')) || 5000;
  var pollUrl = el.getAttribute('data-wayfinder-poll-url');

  // Mode A: no URL — reload after the interval, exactly as before.
  if (!pollUrl) {
    if (intervalMs > 0) {
      setTimeout(function () { location.reload(); }, intervalMs);
    }
    return;
  }

  // Mode B: poll the host's endpoint, reload when it reports a change.
  var statusId = el.getAttribute('data-wayfinder-poll-status-id');
  var statusEl = statusId ? document.getElementById(statusId) : null;
  var maxRetries = Number(el.getAttribute('data-wayfinder-poll-max-retries')) || 100;
  var retries = 0;

  function setStatus(message) {
    if (statusEl) {
      statusEl.textContent = message;
    }
  }

  function poll() {
    if (retries >= maxRetries) {
      setStatus('Please refresh the page to check for updates.');
      return;
    }
    if (document.hidden) {
      setTimeout(poll, intervalMs);
      return;
    }
    retries++;
    setStatus('Checking for updates…');
    fetch(pollUrl, { headers: { 'Accept': 'application/json' } })
      .then(function (response) {
        return response.ok ? response.json() : Promise.reject(response.status);
      })
      .then(function (data) {
        if (data && data.changed) {
          setStatus('Update received — reloading.');
          location.reload();
        } else {
          setTimeout(poll, intervalMs);
        }
      })
      .catch(function () {
        setTimeout(poll, intervalMs);
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', poll);
  } else {
    poll();
  }
})();
