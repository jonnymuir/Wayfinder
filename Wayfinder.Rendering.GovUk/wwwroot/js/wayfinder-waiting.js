// Keeps a waiting page (GovUkWaiting.cs, or a host's own equivalent markup) honest as time passes. The server renders
// the right message for the moment the page was built; this moves it on from "usually takes about a minute" to
// "taking a bit longer than usual" to "much longer than usual" at the expected time and three times it, without a
// reload. It also swaps "Still working on it" for an offline message after two failed checks in a row, which
// wayfinder-poll.js reports with wayfinder:poll-failed and wayfinder:poll-succeeded events.
//
// Every message sits inside one role="status" region and only changes when there is something new to say, so a
// screen reader hears each change once, never a running commentary.
(function () {
  var waiting = document.querySelector('[data-wayfinder-waiting]');
  if (!waiting) {
    return;
  }

  var clock = waiting.querySelector('[data-wayfinder-waiting-clock]');
  if (clock) {
    var expected = Number(clock.getAttribute('data-wayfinder-waiting-expected-seconds')) || 60;
    var waitedAtLoad = Number(clock.getAttribute('data-wayfinder-waiting-waited-seconds')) || 0;
    var loadedAt = Date.now();
    var timer = null;

    var waited = function () {
      return waitedAtLoad + (Date.now() - loadedAt) / 1000;
    };

    var phaseFor = function (seconds) {
      if (seconds > expected * 3) {
        return 'much-longer';
      }
      return seconds > expected ? 'longer' : 'usual';
    };

    var show = function () {
      var current = phaseFor(waited());
      var lines = clock.querySelectorAll('[data-wayfinder-waiting-phase]');
      for (var i = 0; i < lines.length; i++) {
        var matches = lines[i].getAttribute('data-wayfinder-waiting-phase') === current;
        if (lines[i].hidden === matches) {
          lines[i].hidden = !matches;
        }
      }

      clearTimeout(timer);
      var nextAt = current === 'usual' ? expected : current === 'longer' ? expected * 3 : null;
      if (nextAt !== null) {
        timer = setTimeout(show, Math.max(0, nextAt - waited()) * 1000 + 250);
      }
    };

    // A backgrounded tab's timers may not have fired on time, so catch up as soon as it is visible again.
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) {
        show();
      }
    });
    show();
  }

  var working = waiting.querySelector('[data-wayfinder-waiting-working]');
  var offline = waiting.querySelector('[data-wayfinder-waiting-offline]');
  if (working && offline) {
    var setOffline = function (isOffline) {
      if (offline.hidden !== isOffline) {
        return;
      }
      offline.hidden = !isOffline;
      working.hidden = isOffline;
      if (isOffline) {
        waiting.setAttribute('data-wayfinder-waiting-offline-active', '');
      } else {
        waiting.removeAttribute('data-wayfinder-waiting-offline-active');
      }
    };

    document.addEventListener('wayfinder:poll-failed', function (event) {
      if (event.detail && event.detail.consecutiveFailures >= 2) {
        setOffline(true);
      }
    });
    document.addEventListener('wayfinder:poll-succeeded', function () {
      setOffline(false);
    });
  }
})();
