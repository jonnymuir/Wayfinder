#!/usr/bin/env node
// Behavioural test for the device-clock defaults (../wwwroot/js/wayfinder-device-clock.js), driven in a real
// browser with a fixed clock in a timezone far from UTC: "today" and "now" are the visitor's local ones, which
// is the whole point of doing this on the device rather than the server. Uses the markup GovUkFields renders
// for a date input and a text input carrying data-wayfinder-device-default.
// Playwright is resolved from Wayfinder.Editor.Client, which CI already installs with its browsers.
//
// Usage: node test/wayfinder-device-clock.browser.mjs

import http from 'node:http';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(join(__dirname, '..', '..', 'Wayfinder.Editor.Client', 'package.json'))('playwright');
const script = fs.readFileSync(join(__dirname, '..', 'wwwroot', 'js', 'wayfinder-device-clock.js'));

// 23:30 UTC on 6 October is 12:30 on 7 October in Auckland (UTC+13), so UTC and local differ by a day.
const NOW = new Date('2026-10-06T23:30:00Z');
const ZONE = 'Pacific/Auckland';

const form = ({ day = '', month = '', year = '', time = '' }) => `<!doctype html><html><head><meta charset="utf-8"></head><body><form>
<div class="govuk-form-group"><fieldset class="govuk-fieldset" role="group"><legend>Date of the sighting</legend>
<div class="govuk-date-input" id="date" data-wayfinder-device-default="today">
<input id="date-day" name="field:date-day" type="text" value="${day}"><input id="date-month" name="field:date-month" type="text" value="${month}"><input id="date-year" name="field:date-year" type="text" value="${year}">
</div></fieldset></div>
<div class="govuk-form-group"><label for="time">Time of the sighting</label>
<input id="time" name="field:time" type="text" value="${time}" data-wayfinder-device-default="time"></div>
<div class="govuk-form-group"><label for="plain">Plain</label><input id="plain" name="field:plain" type="text" value=""></div>
</form><script src="/clock.js"></script></body></html>`;

const pages = {
  '/': form({}),
  '/saved': form({ day: '3', month: '3', year: '2023', time: '09:05' }),
  '/partial': form({ day: '12' }),
};
const server = http.createServer((req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  if (Object.hasOwn(pages, path)) { res.setHeader('content-type', 'text/html'); return res.end(pages[path]); }
  if (path === '/clock.js') { res.setHeader('content-type', 'text/javascript'); return res.end(script); }
  res.statusCode = 404; res.end();
}).listen(0);
const base = `http://127.0.0.1:${server.address().port}`;

const browser = await chromium.launch();
let failed = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? '  ok  ' : '  FAIL'} ${name}${ok ? '' : ' ' + extra}`); if (!ok) failed++; };
const errors = [];

async function open(path, options = {}) {
  const ctx = await browser.newContext({ timezoneId: ZONE, ...options });
  if (options.javaScriptEnabled !== false) await ctx.clock.setFixedTime(NOW);
  const page = await ctx.newPage();
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto(base + path);
  return page;
}
const date = (page) => Promise.all(['day', 'month', 'year'].map((part) => page.inputValue(`#date-${part}`)));

let page = await open('/');
check('an empty date starts on the device\'s local day, not the UTC one', (await date(page)).join('/') === '7/10/2026', (await date(page)).join('/'));
check('an empty time starts on the device\'s local time', (await page.inputValue('#time')) === '12:30', await page.inputValue('#time'));
check('a field without the attribute is left alone', (await page.inputValue('#plain')) === '');
await page.context().close();

page = await open('/saved');
check('a saved date is not overwritten', (await date(page)).join('/') === '3/3/2023', (await date(page)).join('/'));
check('a saved time is not overwritten', (await page.inputValue('#time')) === '09:05');
await page.context().close();

page = await open('/partial');
check('a date the visitor has started is not completed for them', (await date(page)).join('/') === '12//', (await date(page)).join('/'));
await page.context().close();

page = await open('/', { javaScriptEnabled: false });
check('without script the fields are just empty', (await date(page)).join('') === '' && (await page.inputValue('#time')) === '');
await page.context().close();

page = await open('/');
await page.fill('#time', '14:45');
check('the visitor can change a filled-in time', (await page.inputValue('#time')) === '14:45');
await page.context().close();

check('no page errors', errors.length === 0, errors.join(' | '));
await browser.close(); server.close();
console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
