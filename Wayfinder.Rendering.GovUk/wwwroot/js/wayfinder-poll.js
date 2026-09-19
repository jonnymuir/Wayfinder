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
//
// Backgrounding: a mobile browser or WebView routinely throttles or fully suspends setTimeout
// while the tab/app is hidden, so a citizen who backgrounds the app right as their join gateway
// resolves can return to find a stale page with nothing re-checking (found live: a mobile app
// citizen backgrounded the app during exactly this wait and the page never advanced on return).
// Both modes now also listen for the page becoming visible again (the standard Page Visibility
// API) and act immediately rather than waiting out whatever's left of a timer that may never
// have actually fired on schedule while hidden. On a Capacitor-wrapped native host specifically,
// visibilitychange isn't always reliable for an app-level background/foreground transition (as
// opposed to a browser tab switch, which it handles fine), so this also listens for Capacitor's
// own 'resume' app-lifecycle event directly when window.Capacitor is present — a second, more
// reliable trigger for exactly that host, costing nothing on any other host since the check is
// purely feature-detected.
(function () {
  function onForeground(callback) {
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) {
        callback();
      }
    });

    var Cap = window.Capacitor;
    if (Cap && Cap.Plugins && Cap.Plugins.App && Cap.Plugins.App.addListener) {
      Cap.Plugins.App.addListener('resume', callback);
    }
  }

  var el = document.querySelector('[data-wayfinder-poll-url], [data-wayfinder-poll-interval-ms]');
  if (!el) {
    return;
  }

  var intervalMs = Number(el.getAttribute('data-wayfinder-poll-interval-ms')) || 5000;
  var pollUrl = el.getAttribute('data-wayfinder-poll-url');

  // Mode A: no URL — reload after the interval, exactly as before, but reload immediately on
  // foreground too rather than waiting out a background-throttled timer that may not have
  // actually fired yet.
  if (!pollUrl) {
    if (intervalMs > 0) {
      var reloaded = false;
      var reloadTimer = setTimeout(function () { reloaded = true; location.reload(); }, intervalMs);
      onForeground(function () {
        if (!reloaded) {
          reloaded = true;
          clearTimeout(reloadTimer);
          location.reload();
        }
      });
    }
    return;
  }

  // Mode B: poll the host's endpoint, reload when it reports a change.
  var statusId = el.getAttribute('data-wayfinder-poll-status-id');
  var statusEl = statusId ? document.getElementById(statusId) : null;
  var maxRetries = Number(el.getAttribute('data-wayfinder-poll-max-retries')) || 100;
  var retries = 0;
  var pollTimer = null;
  var inFlight = false;
  // Set when a foreground/resume event asks for an immediate check while a request from the
  // previous cycle is still in flight — rather than starting a second overlapping fetch, or
  // (worse) falling back to a full-interval wait, the in-flight request's own completion handler
  // sees this and polls again straight away once it's done.
  var pendingPoll = false;

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
    if (inFlight) {
      pendingPoll = true;
      return;
    }
    if (document.hidden) {
      pollTimer = setTimeout(poll, intervalMs);
      return;
    }
    inFlight = true;
    retries++;
    setStatus('Checking for updates…');
    fetch(pollUrl, { headers: { 'Accept': 'application/json' } })
      .then(function (response) {
        return response.ok ? response.json() : Promise.reject(response.status);
      })
      .then(function (data) {
        inFlight = false;
        if (data && data.changed) {
          setStatus('Update received — reloading.');
          location.reload();
        } else if (pendingPoll) {
          pendingPoll = false;
          poll();
        } else {
          pollTimer = setTimeout(poll, intervalMs);
        }
      })
      .catch(function () {
        inFlight = false;
        if (pendingPoll) {
          pendingPoll = false;
          poll();
        } else {
          pollTimer = setTimeout(poll, intervalMs);
        }
      });
  }

  onForeground(function () {
    if (pollTimer) {
      clearTimeout(pollTimer);
      pollTimer = null;
    }
    poll();
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', poll);
  } else {
    poll();
  }
})();
