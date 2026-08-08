// A join gateway's waiting stage (RenderWaiting in GovUkComponents.cs) carries its own authored
// poll interval as data-wayfinder-poll-interval-ms — Wayfinder-owned behaviour, not a host
// decision, so this script (not host-specific code) is what every host loads to honour it. A
// host with no client-side router treats "poll" as reload the page after that interval — the
// server re-evaluates the request's cursor state on every request, so a still-waiting applicant
// gets the same page back (with a fresh timer) and one whose case has moved on gets the next
// stage automatically, with no manual refresh. A host with its own SPA-style router would
// instead re-fetch and re-render in place; this script is the plain-HTML-host default.
// Shipped as this package's own static web asset
// (/_content/Wayfinder.Rendering.GovUk/js/wayfinder-poll.js).
var pollTarget = document.querySelector('[data-wayfinder-poll-interval-ms]');
if (pollTarget) {
  var intervalMs = Number(pollTarget.getAttribute('data-wayfinder-poll-interval-ms'));
  if (intervalMs > 0) {
    setTimeout(function () { location.reload(); }, intervalMs);
  }
}
