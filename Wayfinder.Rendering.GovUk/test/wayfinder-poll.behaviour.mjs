#!/usr/bin/env node
// Behavioural tests for the waiting stage's poll script (../wwwroot/js/wayfinder-poll.js) —
// specifically its foreground/resume handling, since that's the part a real browser can't easily
// exercise in CI (no Capacitor bridge, and simulating backgrounding needs to fake both the Page
// Visibility API and setTimeout throttling). Runs the real script source in a small sandboxed
// vm context with hand-mocked document/window/fetch/timers — plain Node, no test framework or
// npm install required, matching this package's own no-build-step convention for its static JS
// assets (see wayfinder-calculations.conformance.mjs alongside this file).
//
// Usage: node test/wayfinder-poll.behaviour.mjs

import fs from 'node:fs';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const src = fs.readFileSync(join(__dirname, '..', 'wwwroot', 'js', 'wayfinder-poll.js'), 'utf8');

function flush() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function buildSandbox({ pollUrl, intervalMs, capacitor, fetchDelayed }) {
  const listeners = { visibilitychange: [] };
  const el = {
    getAttribute(name) {
      if (name === 'data-wayfinder-poll-interval-ms') return String(intervalMs ?? 1000);
      if (name === 'data-wayfinder-poll-url') return pollUrl ?? null;
      return null;
    },
  };
  let hidden = false;
  const timers = [];
  let fetchCalls = 0;
  let reloadCalls = 0;
  const pendingFetchResolvers = [];

  const documentMock = {
    readyState: 'complete',
    get hidden() { return hidden; },
    querySelector: () => el,
    getElementById: () => null,
    addEventListener(type, cb) {
      if (!listeners[type]) listeners[type] = [];
      listeners[type].push(cb);
    },
  };

  const sandbox = {
    document: documentMock,
    window: { Capacitor: capacitor },
    location: { reload: () => { reloadCalls++; } },
    fetch: () => {
      fetchCalls++;
      if (fetchDelayed) {
        return new Promise((resolve) => {
          pendingFetchResolvers.push(() => resolve({ ok: true, json: () => Promise.resolve({ changed: false }) }));
        });
      }
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ changed: false }) });
    },
    setTimeout: (fn) => { const id = { fn }; timers.push(id); return id; },
    clearTimeout: (id) => { const i = timers.indexOf(id); if (i >= 0) timers.splice(i, 1); },
    console,
  };

  vm.createContext(sandbox);
  return {
    run: () => vm.runInContext(src, sandbox),
    setHidden: (v) => { hidden = v; },
    fireVisibilityChange: () => listeners.visibilitychange.forEach((cb) => cb()),
    fireCapacitorResume: () => (capacitor?.__resumeListeners ?? []).forEach((cb) => cb()),
    getFetchCalls: () => fetchCalls,
    getReloadCalls: () => reloadCalls,
    resolveNextFetch: () => { const r = pendingFetchResolvers.shift(); if (r) r(); },
    advanceAllTimers: () => { while (timers.length) { const t = timers.shift(); t.fn(); } },
  };
}

let failed = 0;
let total = 0;
function check(condition, message) {
  total++;
  if (!condition) {
    failed++;
    console.error(`FAIL: ${message}`);
  }
}

async function main() {
  // Mode A — foreground triggers an immediate reload instead of waiting out a timer that may
  // never actually have fired on schedule while backgrounded.
  {
    const h = buildSandbox({ pollUrl: null, intervalMs: 999999 });
    h.run();
    h.setHidden(true);
    h.fireVisibilityChange();
    check(h.getReloadCalls() === 0, 'Mode A: must not reload while still hidden');
    h.setHidden(false);
    h.fireVisibilityChange();
    check(h.getReloadCalls() === 1, `Mode A: must reload immediately on foreground, got ${h.getReloadCalls()}`);
  }

  // Mode A — no Capacitor, page never hidden: the original timer-based reload still fires
  // (regression check — the foreground handling must be additive, not a replacement).
  {
    const h = buildSandbox({ pollUrl: null, intervalMs: 50 });
    h.run();
    h.advanceAllTimers();
    check(h.getReloadCalls() === 1, `Mode A: original timer-based reload must still work, got ${h.getReloadCalls()}`);
  }

  // Mode B — foreground triggers an immediate poll rather than waiting out the interval.
  {
    const h = buildSandbox({ pollUrl: '/status', intervalMs: 999999 });
    h.run();
    await flush();
    check(h.getFetchCalls() === 1, `Mode B: initial poll must fire once on load, got ${h.getFetchCalls()}`);
    h.setHidden(true);
    h.fireVisibilityChange();
    check(h.getFetchCalls() === 1, 'Mode B: must not fetch while still hidden');
    h.setHidden(false);
    h.fireVisibilityChange();
    await flush();
    check(h.getFetchCalls() === 2, `Mode B: must fetch immediately on foreground, got ${h.getFetchCalls()}`);
  }

  // Mode B — foreground fires while a fetch from the previous cycle is still in flight: must not
  // start a second overlapping fetch, and must poll again immediately once that one settles
  // rather than falling back to a full-interval wait.
  {
    const h = buildSandbox({ pollUrl: '/status', intervalMs: 999999, fetchDelayed: true });
    h.run();
    check(h.getFetchCalls() === 1, 'initial fetch must have started');
    h.setHidden(true);
    h.fireVisibilityChange();
    h.setHidden(false);
    h.fireVisibilityChange();
    check(h.getFetchCalls() === 1, 'must not start a second overlapping fetch while one is in flight');
    h.resolveNextFetch();
    await flush();
    check(h.getFetchCalls() === 2, `once the in-flight fetch settles, must poll again immediately, got ${h.getFetchCalls()}`);
  }

  // Capacitor's own 'resume' event is a second, independently-wired trigger for exactly the case
  // visibilitychange isn't always reliable for on a native-wrapped host.
  {
    const resumeListeners = [];
    const capacitor = {
      Plugins: { App: { addListener: (event, cb) => { if (event === 'resume') resumeListeners.push(cb); } } },
      __resumeListeners: resumeListeners,
    };
    const h = buildSandbox({ pollUrl: null, intervalMs: 999999, capacitor });
    h.run();
    check(resumeListeners.length === 1, 'Capacitor resume listener must be registered when window.Capacitor is present');
    h.fireCapacitorResume();
    check(h.getReloadCalls() === 1, `Capacitor resume must trigger an immediate reload, got ${h.getReloadCalls()}`);
  }

  console.log(`\n${total - failed} passed, ${failed} failed, ${total} total.`);
  process.exit(failed === 0 ? 0 : 1);
}

main();
