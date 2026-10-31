#!/usr/bin/env node
// Behavioural tests for the waiting page's message script (../wwwroot/js/wayfinder-waiting.js): which message a
// person sees as the wait runs on, and the offline message. Runs the real script in a small vm sandbox with a
// hand-made DOM, fake clock and fake timers, in the same no-dependency style as wayfinder-poll.behaviour.mjs.
//
// Usage: node test/wayfinder-waiting.behaviour.mjs

import fs from 'node:fs';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const src = fs.readFileSync(join(__dirname, '..', 'wwwroot', 'js', 'wayfinder-waiting.js'), 'utf8');

function element(attrs = {}, hidden = false) {
  const attributes = { ...attrs };
  return {
    hidden,
    children: [],
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    setAttribute: (name, value) => { attributes[name] = value; },
    removeAttribute: (name) => { delete attributes[name]; },
    hasAttribute: (name) => name in attributes,
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; },
    querySelectorAll(selector) {
      const name = selector.slice(1, -1);
      const found = [];
      const walk = (node) => node.children.forEach((child) => { if (child.hasAttribute(name)) found.push(child); walk(child); });
      walk(this);
      return found;
    },
  };
}

function buildPage({ expected = 60, waited = 0, phase = 'usual' } = {}) {
  const lines = ['usual', 'longer', 'much-longer'].map((p) => element({ 'data-wayfinder-waiting-phase': p }, p !== phase));
  const clock = element({ 'data-wayfinder-waiting-clock': '', 'data-wayfinder-waiting-expected-seconds': String(expected), 'data-wayfinder-waiting-waited-seconds': String(waited) });
  const working = element({ 'data-wayfinder-waiting-working': '' });
  const offline = element({ 'data-wayfinder-waiting-offline': '' }, true);
  clock.children.push(working, offline, ...lines);
  const waiting = element({ 'data-wayfinder-waiting': '' });
  waiting.children.push(clock);

  let now = 1_000_000;
  let timers = [];
  const listeners = {};
  const documentMock = {
    hidden: false,
    querySelector: (selector) => (selector === '[data-wayfinder-waiting]' ? waiting : null),
    addEventListener: (type, cb) => { (listeners[type] ??= []).push(cb); },
  };
  const sandbox = {
    document: documentMock,
    Date: { now: () => now },
    setTimeout: (fn, ms) => { const t = { fn, at: now + ms }; timers.push(t); return t; },
    clearTimeout: (t) => { timers = timers.filter((x) => x !== t); },
    Math,
    Number,
  };
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox);

  return {
    visible: () => lines.filter((l) => !l.hidden).map((l) => l.getAttribute('data-wayfinder-waiting-phase')),
    advance(seconds) {
      now += seconds * 1000;
      for (const due of timers.filter((t) => t.at <= now)) { timers = timers.filter((x) => x !== due); due.fn(); }
    },
    fire: (type, detail) => (listeners[type] ?? []).forEach((cb) => cb({ detail })),
    offlineShown: () => !offline.hidden && working.hidden,
    waiting,
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

{
  const page = buildPage({ expected: 60, waited: 10 });
  check(page.visible().join() === 'usual', `within the usual time it says how long it usually takes, got ${page.visible()}`);
  page.advance(55);
  check(page.visible().join() === 'longer', `past the usual time it says it is taking a bit longer, got ${page.visible()}`);
  page.advance(130);
  check(page.visible().join() === 'much-longer', `past three times the usual time it says much longer, got ${page.visible()}`);
}

{
  const page = buildPage({ expected: 60, waited: 200, phase: 'much-longer' });
  check(page.visible().join() === 'much-longer', 'a page built late in a long wait keeps the message the server chose');
}

{
  const page = buildPage({ expected: 60, waited: 90, phase: 'usual' });
  check(page.visible().join() === 'longer', 'time already waited before the page loaded counts towards the next message');
}

{
  const page = buildPage();
  page.fire('wayfinder:poll-failed', { consecutiveFailures: 1 });
  check(!page.offlineShown(), 'one failed check is not enough to say the service cannot be reached');
  page.fire('wayfinder:poll-failed', { consecutiveFailures: 2 });
  check(page.offlineShown(), 'two failed checks in a row say the service cannot be reached');
  check(page.waiting.hasAttribute('data-wayfinder-waiting-offline-active'), 'the indicator stops while offline');
  page.fire('wayfinder:poll-succeeded', {});
  check(!page.offlineShown(), 'a successful check goes back to "Still working on it"');
}

console.log(`${total - failed} passed, ${failed} failed, ${total} total.`);
process.exit(failed ? 1 : 0);
