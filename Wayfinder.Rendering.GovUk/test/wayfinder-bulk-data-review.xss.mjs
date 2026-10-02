#!/usr/bin/env node
// Regression test for stored XSS in the bulk-data review script (../wwwroot/js/wayfinder-bulk-data-review.js).
// A cell value in an uploaded dataset is attacker-controlled, and the script renders it into HTML
// attributes (value="…", id="…"), so escaping that only covers <, > and & is not enough: a value
// containing a double quote broke out of the attribute and injected an event handler.
//
// Drives the REAL script in a real browser (the bug is in the browser's HTML parsing, so a mock DOM
// proves nothing) against a hostile row, with and without a CSP, and asserts nothing was injected.
// Playwright is resolved from Wayfinder.Editor.Client, which CI already installs with its browsers.
//
// Usage: node test/wayfinder-bulk-data-review.xss.mjs

import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const scriptSource = fs.readFileSync(join(__dirname, '..', 'wwwroot', 'js', 'wayfinder-bulk-data-review.js'), 'utf8');
const { chromium } = createRequire(join(__dirname, '..', '..', 'Wayfinder.Editor.Client', 'package.json'))('playwright');

const HOSTILE_VALUE = 'x" autofocus onfocus="window.__pwned=1" data-wayfinder-bulk-review-input="other';
const HOSTILE_ROW_KEY = 'r"><img src=x onerror="window.__pwned=1">';

const markup = `<!doctype html><html><body>
<div data-wayfinder-bulk-review data-wayfinder-bulk-review-api="/api" data-wayfinder-bulk-review-page-size="20"
     data-wayfinder-bulk-review-synced-label="Synced" data-wayfinder-bulk-review-pending-label="Pending"
     data-wayfinder-bulk-review-since-label="today">
  <div data-wayfinder-bulk-review-summary></div>
  <div data-wayfinder-bulk-review-controls hidden></div>
  <div data-wayfinder-bulk-review-rows></div>
  <div data-wayfinder-bulk-review-pagination hidden>
    <span data-wayfinder-bulk-review-page-status></span>
    <span data-wayfinder-bulk-review-prev-wrapper><a data-wayfinder-bulk-review-prev></a></span>
    <span data-wayfinder-bulk-review-next-wrapper><a data-wayfinder-bulk-review-next></a></span>
  </div>
</div>
<script src="/wayfinder-bulk-data-review.js"></script></body></html>`;

const summary = {
  columns: [{ key: 'name', title: 'Name', role: 'Data', editable: true, visible: true }],
  totalRowCount: 1, errorRowCount: 0, warningRowCount: 0, acceptedRowCount: 1, dirtyRowCount: 0,
};
const rows = {
  rows: [{ rowKey: HOSTILE_ROW_KEY, currentValues: { name: HOSTILE_VALUE }, hasError: false, hasWarning: false }],
  totalMatchingRowCount: 1, pageIndex: 0,
};

let failures = 0;
function check(name, ok, detail = '') {
  console.log(`${ok ? '  ok ' : 'FAIL'}  ${name}${ok ? '' : ` — ${detail}`}`);
  if (!ok) failures++;
}

const browser = await chromium.launch();
for (const csp of [null, "script-src 'self'"]) {
  const label = csp ? 'with a strict CSP' : 'with no CSP';
  const page = await browser.newPage();
  await page.route('http://wayfinder.test/**', (route) => {
    const { pathname } = new URL(route.request().url());
    if (pathname === '/wayfinder-bulk-data-review.js') return route.fulfill({ contentType: 'text/javascript', body: scriptSource });
    if (pathname === '/api/summary') return route.fulfill({ contentType: 'application/json', body: JSON.stringify(summary) });
    if (pathname === '/api/rows') return route.fulfill({ contentType: 'application/json', body: JSON.stringify(rows) });
    return route.fulfill({ contentType: 'text/html', headers: csp ? { 'Content-Security-Policy': csp } : {}, body: markup });
  });
  await page.goto('http://wayfinder.test/');
  await page.locator('[data-wayfinder-bulk-review-rows] input').waitFor();
  await page.locator('[data-wayfinder-bulk-review-rows] input').focus();

  const result = await page.evaluate(() => {
    const input = document.querySelector('[data-wayfinder-bulk-review-rows] input');
    return {
      pwned: window.__pwned === 1,
      attributes: [...input.attributes].map((a) => a.name).sort(),
      value: input.value,
      injectedImages: document.querySelectorAll('[data-wayfinder-bulk-review-rows] img').length,
      title: document.querySelector('.wayfinder-bulk-review__card-title')?.textContent,
    };
  });

  check(`${label}: a hostile cell value does not run script`, result.pwned === false);
  check(`${label}: a hostile cell value injects no attributes into the input`,
    JSON.stringify(result.attributes) === JSON.stringify(['class', 'data-wayfinder-bulk-review-input', 'id', 'value']), result.attributes.join(','));
  check(`${label}: the cell value is shown to the caseworker exactly as uploaded`, result.value === HOSTILE_VALUE, result.value);
  check(`${label}: a hostile row key injects no elements`, result.injectedImages === 0 && result.title === HOSTILE_ROW_KEY, `${result.injectedImages} img, title=${result.title}`);
  await page.close();
}
await browser.close();

console.log(failures === 0 ? '\nAll bulk-data review escaping checks passed.' : `\n${failures} check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
