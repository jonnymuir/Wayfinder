#!/usr/bin/env node
// Behavioural test for the stat-group tiles (../wwwroot/css/wayfinder-components.css), driven in a real browser:
// whether a long value stays inside its card is layout, which only a browser computes. Uses the markup
// GovUkComponents.RenderStatGroup renders and the shipped stylesheet, in a column as narrow as a phone.
// Playwright is resolved from Wayfinder.Editor.Client, which CI already installs with its browsers.
//
// Usage: node test/wayfinder-stat-card.browser.mjs

import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(join(__dirname, '..', '..', 'Wayfinder.Editor.Client', 'package.json'))('playwright');
const css = fs.readFileSync(join(__dirname, '..', 'wwwroot', 'css', 'wayfinder-components.css'), 'utf8');

const card = (label, value) => `<div class="wayfinder-stat-card" data-wayfinder-stat="${label}">
  <div class="wayfinder-stat-card__label">${label}</div><div class="wayfinder-stat-card__value">${value}</div></div>`;
const html = (width) => `<!doctype html><html><head><meta charset="utf-8"><style>${css} body{font-family:sans-serif;margin:0}</style></head>
<body><div style="width:${width}px"><div class="wayfinder-stat-group" role="group">
${card('Common name', 'Peacock')}${card('Scientific name', 'Aglais io')}${card('Where', '53.792035,-1.663200')}${card('Email', 'someone.with.a.long.name@example.organisation.org')}
</div></div></body></html>`;

const browser = await chromium.launch();
let failed = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? '  ok  ' : '  FAIL'} ${name}${ok ? '' : ' ' + extra}`); if (!ok) failed++; };

for (const width of [360, 727]) {
  const page = await browser.newPage();
  await page.setContent(html(width));
  // The value is a block box, so its own rectangle never leaves the card; it is the text inside that
  // spills, which scrollWidth reports even though overflow is visible.
  const overflow = await page.$$eval('.wayfinder-stat-card', (cards) => cards.map((c) => {
    const card = c.getBoundingClientRect();
    const value = c.querySelector('.wayfinder-stat-card__value');
    const text = document.createRange();
    text.selectNodeContents(value);
    return { label: c.dataset.wayfinderStat, outside: Math.max(0, text.getBoundingClientRect().right - card.right) };
  }));
  for (const { label, outside } of overflow) check(`"${label}" stays inside its card in a ${width}px column`, outside < 1, `by ${Math.round(outside)}px`);
  const page2 = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
  check(`the ${width}px page does not scroll sideways`, page2);
  await page.close();
}

await browser.close();
console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
