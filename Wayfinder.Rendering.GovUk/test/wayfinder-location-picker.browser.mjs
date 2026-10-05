#!/usr/bin/env node
// Behavioural test for the location picker (../wwwroot/location-picker/wayfinder-location-picker.js), driven in a real
// browser: the geolocation prompt, the map and the text field's two-way sync only exist in one. Serves the
// built bundle with the markup GovUkLocationPickerField renders, a mocked device location, and a local tile
// route so nothing touches the network. Also proves the plain input still works with no script at all.
// Playwright is resolved from Wayfinder.Editor.Client, which CI already installs with its browsers.
//
// Usage: node test/wayfinder-location-picker.browser.mjs

import http from 'node:http';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(join(__dirname, '..', '..', 'Wayfinder.Editor.Client', 'package.json'))('playwright');
const js = join(__dirname, '..', 'wwwroot', 'location-picker') + '/';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const EXISTING_VALUE = '52.2053,0.1218';
const page = (value) => `<!doctype html><html><head><meta charset="utf-8">
<meta name="wayfinder-map-tile-url" content="/tiles/{z}/{x}/{y}.png"><meta name="wayfinder-map-attribution" content="test tiles"></head><body>
<form><div class="govuk-form-group" data-wayfinder-location-picker>
<label class="govuk-label" for="location">Where did you see it?</label>
<div id="location-hint" class="govuk-hint">Latitude and longitude</div>
<input class="govuk-input" id="location" name="field:location" type="text" value="${value}" data-wayfinder-location-input aria-describedby="location-hint" required>
</div></form><script type="module" src="/lp/wayfinder-location-picker.js"></script></body></html>`;
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  // Fixed fixtures only: nothing from the request is ever written into the page.
  const fixtures = { '/': '', '/existing': EXISTING_VALUE };
  if (Object.hasOwn(fixtures, url.pathname)) { res.setHeader('content-type', 'text/html'); return res.end(page(fixtures[url.pathname])); }
  if (url.pathname.startsWith('/tiles/')) { res.setHeader('content-type', 'image/png'); return res.end(png); }
  if (url.pathname.startsWith('/lp/')) {
    const f = js + url.pathname.slice(4); if (!fs.existsSync(f)) { res.statusCode = 404; return res.end(); }
    res.setHeader('content-type', f.endsWith('.css') ? 'text/css' : 'text/javascript'); return res.end(fs.readFileSync(f));
  }
  res.statusCode = 404; res.end();
}).listen(0);
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();
let failed = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? '  ok  ' : '  FAIL'} ${name}${ok ? '' : ' ' + extra}`); if (!ok) failed++; };
const errors = [];
async function open(ctxOpts, path = '/') {
  const ctx = await browser.newContext(ctxOpts);
  const p = await ctx.newPage();
  p.on('pageerror', (e) => errors.push(e.message));
  p.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await p.goto(`${base}${path}`);
  return p;
}
const geo = { permissions: ['geolocation'], geolocation: { latitude: 51.5074, longitude: -0.1278, accuracy: 25 } };

// 1. Empty field + permission granted: auto-fills from the device and announces it.
let p = await open(geo);
await p.waitForFunction(() => document.querySelector('#location').value !== '', null, { timeout: 8000 }).catch(() => {});
check('auto-fills an empty field from the device location', (await p.inputValue('#location')) === '51.507400,-0.127800', await p.inputValue('#location'));
check('announces the source and accuracy', /accurate to about 25 metres/.test(await p.getByRole('status').innerText()));
check('renders a map group labelled with the field and the keyboard alternative', /type it in the field above/.test(await p.getByRole('group').getAttribute('aria-label')));
check('adds a current-location button', await p.getByRole('button', { name: 'Use my current location' }).isVisible());
check('the map actually rendered a canvas', (await p.locator('canvas').count()) === 1);

// 2. Clicking the map sets the field and fires the event with source=map.
await p.evaluate(() => { window.__events = []; document.addEventListener('wayfinder:location-changed', (e) => window.__events.push(e.detail)); });
const box = await p.locator('canvas').boundingBox();
await p.mouse.click(box.x + box.width * 0.75, box.y + box.height * 0.5);
await p.waitForFunction(() => window.__events.length > 0);
const ev = await p.evaluate(() => window.__events[0]);
check('clicking the map reports source=map', ev.source === 'map', JSON.stringify(ev));
check('clicking the map writes the canonical value', /^-?\d+\.\d{6},-?\d+\.\d{6}$/.test(await p.inputValue('#location')), await p.inputValue('#location'));

// 3. Typing a point moves the pin and reports source=typed; garbage does not fire.
await p.evaluate(() => { window.__events.length = 0; });
await p.fill('#location', '54.5, -3.1'); await p.locator('#location').blur();
await p.waitForFunction(() => window.__events.length > 0);
check('typing a point reports source=typed', (await p.evaluate(() => window.__events[0].source)) === 'typed');
await p.evaluate(() => { window.__events.length = 0; });
await p.fill('#location', 'nonsense'); await p.locator('#location').blur();
await p.waitForTimeout(150);
check('typing nonsense fires nothing and leaves the text for the server to reject', (await p.evaluate(() => window.__events.length)) === 0 && (await p.inputValue('#location')) === 'nonsense');

// 4. Button re-locates.
await p.getByRole('button', { name: 'Use my current location' }).click();
await p.waitForFunction(() => document.querySelector('#location').value === '51.507400,-0.127800');
check('the button sets the device location again', true);
await p.context().close();

// 5. Permission denied: field stays empty, a clear message, page still usable.
p = await open({ permissions: [] });
await p.waitForFunction(() => /declined|could not|too long/.test(document.querySelector('[role=status]')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
check('a declined permission leaves the field empty with guidance', (await p.inputValue('#location')) === '' && /declined/.test(await p.getByRole('status').innerText()), await p.getByRole('status').innerText());
await p.context().close();

// 6. Existing value is kept (no auto-locate overwrite) and shown on the map.
p = await open(geo, '/existing');
await p.waitForTimeout(400);
check('an existing value is not overwritten by the device location', (await p.inputValue('#location')) === EXISTING_VALUE);
await p.context().close();

// 7. No script: the markup alone is a working labelled text input (the script never ran here).
const bare = await browser.newContext({ javaScriptEnabled: false }); const bp = await bare.newPage(); await bp.goto(base + '/');
check('without script the plain labelled input still works', await bp.getByLabel('Where did you see it?').isVisible());
await bare.close();

check('no page or console errors', errors.length === 0, errors.join(' | '));
await browser.close(); server.close();
process.exit(failed ? 1 : 0);
