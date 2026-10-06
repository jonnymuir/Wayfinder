#!/usr/bin/env node
// Behavioural test for the location picker and the read-only map (../wwwroot/location-picker/wayfinder-location-picker.js),
// driven in a real browser: the geolocation prompt, the map and the text field's two-way sync only exist in one. Serves the
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
const page = (value, searchMeta = '<meta name="wayfinder-map-search-url" content="/search?q={query}">') => `<!doctype html><html><head><meta charset="utf-8">
<meta name="wayfinder-map-tile-url" content="/tiles/{z}/{x}/{y}.png"><meta name="wayfinder-map-attribution" content="test tiles">${searchMeta}</head><body>
<form onsubmit="window.__submits = (window.__submits ?? 0) + 1; return false"><div class="govuk-form-group" data-wayfinder-location-picker>
<label class="govuk-label" for="location">Where did you see it?</label>
<div id="location-hint" class="govuk-hint">Latitude and longitude</div>
<input class="govuk-input" id="location" name="field:location" type="text" value="${value}" data-wayfinder-location-input aria-describedby="location-hint" required>
</div></form><div style="height:2500px"></div><script type="module" src="/lp/wayfinder-location-picker.js"></script></body></html>`;
// The markup GovUkStatGroup renders for a tile with display "map".
const tile = (value, extra = '') => `<div class="wayfinder-stat-card wayfinder-stat-card--map" data-wayfinder-stat="Where"${extra}>
<div class="wayfinder-stat-card__label">Where</div><div class="wayfinder-stat-card__value wayfinder-stat-card__value--point">${value}</div>
<div class="wayfinder-location-view" data-wayfinder-location-map data-wayfinder-location="${value}" data-wayfinder-label="Where"></div></div>`;
const viewPage = (value) => `<!doctype html><html><head><meta charset="utf-8">
<meta name="wayfinder-map-tile-url" content="/tiles/{z}/{x}/{y}.png"><meta name="wayfinder-map-attribution" content="test tiles"></head><body>
<div class="wayfinder-stat-group" role="group">${tile(value)}</div><script type="module" src="/lp/wayfinder-location-picker.js"></script></body></html>`;
const sendHtml = (res, body) => { res.setHeader('content-type', 'text/html'); res.end(body); };
// Fixed fixtures only: nothing from the request is ever written into the page.
const pages = {
  '/': () => page(''),
  '/existing': () => page(EXISTING_VALUE),
  '/nosearch': () => page('', '<meta name="wayfinder-map-search-url" content="">'),
  '/view': () => viewPage(EXISTING_VALUE),
  '/view-bad': () => viewPage('nonsense'),
};
const PLACES = {
  leeds: [
    { lat: '53.7996', lon: '-1.5491', display_name: 'Leeds, West Yorkshire, England' },
    { lat: '53.8008', lon: '-1.5489', display_name: 'Leeds railway station, Leeds' },
  ],
};
function sendSearch(res, query) {
  if (query === 'boom') { res.statusCode = 500; return res.end('no'); }
  res.setHeader('content-type', 'application/json');
  return res.end(JSON.stringify(PLACES[query] ?? []));
}
function sendAsset(res, name) {
  const file = js + name;
  if (!fs.existsSync(file)) { res.statusCode = 404; return res.end(); }
  res.setHeader('content-type', file.endsWith('.css') ? 'text/css' : 'text/javascript');
  return res.end(fs.readFileSync(file));
}
const server = http.createServer((req, res) => {
  const { pathname, searchParams } = new URL(req.url, 'http://x');
  if (Object.hasOwn(pages, pathname)) return sendHtml(res, pages[pathname]());
  if (pathname === '/search') return sendSearch(res, (searchParams.get('q') ?? '').toLowerCase());
  if (pathname.startsWith('/tiles/')) { res.setHeader('content-type', 'image/png'); return res.end(png); }
  if (pathname.startsWith('/lp/')) return sendAsset(res, pathname.slice(4));
  res.statusCode = 404; return res.end();
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
check('announces the source and accuracy', /accurate to about 25 metres/.test(await p.locator('.wayfinder-location__status').innerText()));
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
check('a declined permission leaves the field empty with guidance', (await p.inputValue('#location')) === '' && /declined/.test(await p.locator('.wayfinder-location__status').innerText()), await p.locator('.wayfinder-location__status').innerText());
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

// 9a. A mouse drag pans the map and reports where it is centred (the hook the touch test below relies on).
p = await open({}, '/existing');
await p.waitForFunction(() => document.querySelector('.wayfinder-location__map')?.dataset.wayfinderCentre);
const centreBefore = await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre');
const dragBox = await p.locator('.wayfinder-location__map canvas').boundingBox();
await p.mouse.move(dragBox.x + dragBox.width * 0.5, dragBox.y + dragBox.height * 0.75);
await p.mouse.down();
await p.mouse.move(dragBox.x + dragBox.width * 0.5, dragBox.y + dragBox.height * 0.25, { steps: 12 });
await p.mouse.up();
await p.waitForFunction((was) => document.querySelector('.wayfinder-location__map').dataset.wayfinderCentre !== was, centreBefore, { timeout: 4000 }).catch(() => {});
check('dragging with a mouse pans the map and updates the centre hook', (await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre')) !== centreBefore);
check('and a drag does not change the stored value', (await p.inputValue('#location')) === EXISTING_VALUE);
await p.context().close();

// 9. One-finger pan on a phone. OpenLayers asks the browser to handle one-finger drags (touch-action: pan-x pan-y),
// so without our override the page scrolls and the map only ever sees a pinch. Driven with real touch events.
async function drag(cdp, fromX, fromY, dy) {
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: fromX, y: fromY }] });
  for (let i = 1; i <= 12; i++) await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: fromX, y: fromY + (dy * i) / 12 }] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
}
{
  const ctx = await browser.newContext({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });
  p = await ctx.newPage();
  p.on('pageerror', (e) => errors.push(e.message));
  await p.goto(`${base}/existing`);
  const finger = await ctx.newCDPSession(p);
  await p.waitForFunction(() => document.querySelector('.wayfinder-location__map')?.dataset.wayfinderCentre);
  const before = await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre');
  const mapBox = await p.locator('.wayfinder-location__map canvas').boundingBox();
  await p.evaluate(() => window.scrollTo(0, 0));
  await drag(finger, mapBox.x + mapBox.width / 2, mapBox.y + mapBox.height * 0.75, -mapBox.height * 0.5);
  await p.waitForFunction((was) => document.querySelector('.wayfinder-location__map').dataset.wayfinderCentre !== was, before, { timeout: 4000 }).catch(() => {});
  const after = await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre');
  check('the map tells the browser it owns drags (touch-action: none), which is what a real phone obeys', (await p.locator('.wayfinder-location__map .ol-viewport').evaluate((el) => getComputedStyle(el).touchAction)) === 'none');
  check('a one-finger drag on the map pans the map', before !== after, `${before} -> ${after}`);
  check('and does not scroll the page', (await p.evaluate(() => window.scrollY)) === 0, String(await p.evaluate(() => window.scrollY)));
  check('the pan does not change the stored value (only a click or the pin does)', (await p.inputValue('#location')) === EXISTING_VALUE);
  const box = await p.locator('.wayfinder-location__map').boundingBox();
  check('the map leaves room to scroll the page (at most 55% of the screen)', box.height <= 844 * 0.55 + 1, String(box.height));
  await drag(finger, 195, 780, -300);
  await p.waitForTimeout(300);
  check('a drag outside the map still scrolls the page', (await p.evaluate(() => window.scrollY)) > 0);
  await ctx.close();
}

// 10. Place search: results to choose from, never a silent guess, and Enter must not submit the stage form.
p = await open(geo, '/');
check('the search field is labelled', await p.getByLabel('Search for a place or postcode').isVisible());
await p.evaluate(() => { window.__events = []; document.addEventListener('wayfinder:location-changed', (e) => window.__events.push(e.detail)); });
await p.getByLabel('Search for a place or postcode').fill('Leeds');
await p.getByLabel('Search for a place or postcode').press('Enter');
await p.locator('.wayfinder-location__result').first().waitFor({ timeout: 5000 }).catch(() => {});
check('Enter searches and lists the places found', (await p.locator('.wayfinder-location__result').count()) === 2);
check('Enter did not submit the stage form', ((await p.evaluate(() => window.__submits)) ?? 0) === 0);
check('the number of places is announced', /2 places found/.test(await p.locator('.wayfinder-location__search-status').innerText()));
await p.locator('.wayfinder-location__result', { hasText: 'Leeds, West Yorkshire' }).click();
await p.waitForFunction(() => document.querySelector('#location').value === '53.799600,-1.549100').catch(() => {});
check('choosing a place sets the field to that point', (await p.inputValue('#location')) === '53.799600,-1.549100', await p.inputValue('#location'));
check('and clears the stale device-location message above it', (await p.locator('.wayfinder-location__status').innerText()) === '', await p.locator('.wayfinder-location__status').innerText());
check('and reports source=search', (await p.evaluate(() => window.__events.at(-1)?.source)) === 'search');
await p.waitForFunction(() => document.querySelector('.wayfinder-location__map').dataset.wayfinderCentre === '53.799600,-1.549100', null, { timeout: 5000 }).catch(() => {});
check('and moves the map there', (await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre')) === '53.799600,-1.549100', await p.locator('.wayfinder-location__map').getAttribute('data-wayfinder-centre'));
check('the result list closes', await p.locator('.wayfinder-location__results').isHidden());

await p.getByLabel('Search for a place or postcode').fill('nowhere');
await p.getByRole('button', { name: 'Search', exact: true }).click();
await p.waitForFunction(() => /No places found/.test(document.querySelector('.wayfinder-location__search-status')?.textContent ?? ''));
check('a search with no match says so', true);
await p.getByLabel('Search for a place or postcode').fill('boom');
await p.getByRole('button', { name: 'Search', exact: true }).click();
await p.waitForFunction(() => /not available just now/.test(document.querySelector('.wayfinder-location__search-status')?.textContent ?? ''));
check('a failing search service gets an honest message, and the picker still works', (await p.inputValue('#location')) === '53.799600,-1.549100');
await p.getByLabel('Search for a place or postcode').fill('   ');
await p.getByRole('button', { name: 'Search', exact: true }).click();
check('an empty query asks for one', /Enter a town/.test(await p.locator('.wayfinder-location__search-status').innerText()));
await p.context().close();

p = await open({}, '/nosearch');
await p.waitForTimeout(300);
check('an empty search-url meta turns the search off', (await p.getByLabel('Search for a place or postcode').count()) === 0);
await p.context().close();

// 8. The read-only map: shows the point, keeps the text, and cannot change the value.
p = await open({}, '/view');
await p.waitForFunction(() => document.querySelectorAll('.wayfinder-location-view canvas').length === 1, null, { timeout: 8000 }).catch(() => {});
check('a map tile renders a map for a valid point', (await p.locator('.wayfinder-location-view canvas').count()) === 1);
check('the point is still written out as text', (await p.locator('.wayfinder-stat-card__value').innerText()) === EXISTING_VALUE);
check('the map is labelled with the tile and the point', new RegExp(`Map showing Where at 52\\.205300,0\\.121800`).test(await p.locator('.wayfinder-location-view').getAttribute('aria-label')), await p.locator('.wayfinder-location-view').getAttribute('aria-label'));
const viewBox = await p.locator('.wayfinder-location-view canvas').boundingBox();
await p.mouse.click(viewBox.x + viewBox.width * 0.75, viewBox.y + viewBox.height * 0.5);
await p.waitForTimeout(150);
check('clicking the map does not change the value', (await p.locator('.wayfinder-stat-card__value').innerText()) === EXISTING_VALUE);
await p.context().close();

p = await open({}, '/view-bad');
await p.waitForTimeout(300);
check('a value that is not a point shows no map', (await p.locator('.wayfinder-location-view').count()) === 0);
check('and keeps its text', (await p.locator('.wayfinder-stat-card__value').innerText()) === 'nonsense');
await p.context().close();

// The search test above deliberately makes the search service answer 500, which the browser logs as a console error.
const unexpected = errors.filter((message) => !/status of 500/.test(message));
check('no page or console errors', unexpected.length === 0, unexpected.join(' | '));
await browser.close(); server.close();
process.exit(failed ? 1 : 0);
