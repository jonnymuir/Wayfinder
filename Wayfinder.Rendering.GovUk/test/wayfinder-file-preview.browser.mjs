#!/usr/bin/env node
// Behavioural test for the image file preview (../wwwroot/js/wayfinder-file-preview.js), driven in a real browser
// and served under the strict Content-Security-Policy a host like Prism sends (images only from the page itself
// and data:, scripts and styles only from the page itself), so a preview that needed blob: or an inline style
// would fail here. Uses the markup GovUkFileUploadField renders for an input that accepts images.
// Playwright is resolved from Wayfinder.Editor.Client, which CI already installs with its browsers.
//
// Usage: node test/wayfinder-file-preview.browser.mjs

import http from 'node:http';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(join(__dirname, '..', '..', 'Wayfinder.Editor.Client', 'package.json'))('playwright');
const root = join(__dirname, '..', 'wwwroot');
const script = fs.readFileSync(join(root, 'js', 'wayfinder-file-preview.js'));
const css = fs.readFileSync(join(root, 'css', 'wayfinder-components.css'));

const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'";
const page = (attribute) => `<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="/c.css"></head><body>
<form><div class="govuk-form-group"><label class="govuk-label" for="photo">Photo of the butterfly</label>
<input class="govuk-file-upload" id="photo" name="field:photo" type="file" accept=".jpg,.png"${attribute}></div></form>
<script src="/p.js"></script></body></html>`;
const server = http.createServer((req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  res.setHeader('content-security-policy', CSP);
  if (path === '/') { res.setHeader('content-type', 'text/html'); return res.end(page(' data-wayfinder-file-preview')); }
  if (path === '/unmarked') { res.setHeader('content-type', 'text/html'); return res.end(page('')); }
  if (path === '/p.js') { res.setHeader('content-type', 'text/javascript'); return res.end(script); }
  if (path === '/c.css') { res.setHeader('content-type', 'text/css'); return res.end(css); }
  res.statusCode = 404; res.end();
}).listen(0);
const base = `http://127.0.0.1:${server.address().port}`;

const browser = await chromium.launch();
let failed = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? '  ok  ' : '  FAIL'} ${name}${ok ? '' : ' ' + extra}`); if (!ok) failed++; };
const errors = [];

async function open(path, options = {}) {
  const ctx = await browser.newContext(options);
  const p = await ctx.newPage();
  p.on('pageerror', (e) => errors.push(e.message));
  p.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await p.goto(base + path);
  return p;
}

// A real, large image: made in the page so the test needs no fixture file.
const png = async (p, width, height) => Buffer.from(await p.evaluate(({ width, height }) => {
  const canvas = document.createElement('canvas');
  canvas.width = width; canvas.height = height;
  const context = canvas.getContext('2d');
  context.fillStyle = '#d4351c'; context.fillRect(0, 0, width / 2, height);
  context.fillStyle = '#1d70b8'; context.fillRect(width / 2, 0, width / 2, height);
  return canvas.toDataURL('image/png').split(',')[1];
}, { width, height }), 'base64');

let p = await open('/');
const image = await png(p, 2000, 1000);
await p.setInputFiles('#photo', { name: 'IMG_2968.png', mimeType: 'image/png', buffer: image });
await p.locator('.wayfinder-file-preview__image').waitFor({ state: 'visible', timeout: 8000 }).catch(() => {});
const shown = await p.locator('.wayfinder-file-preview__image').evaluate((img) => ({ src: img.src.slice(0, 22), w: img.naturalWidth, h: img.naturalHeight, alt: img.alt })).catch(() => null);
check('a chosen image is shown as a small picture', shown !== null && shown.w > 0, JSON.stringify(shown));
check('the picture is a data: URL, which the strict CSP allows', shown?.src.startsWith('data:image/jpeg'), shown?.src);
check('the picture is scaled down (2000px wide became 640px) and keeps its shape', shown?.w === 640 && shown?.h === 320, `${shown?.w}x${shown?.h}`);
check('the picture has a descriptive alt text', /Preview of the file you chose/.test(shown?.alt ?? ''));
check('the caption says what was chosen', /Chosen: IMG_2968\.png, \d+ KB/.test(await p.locator('.wayfinder-file-preview__caption').innerText()), await p.locator('.wayfinder-file-preview__caption').innerText());
check('the caption is announced as a status', (await p.getByRole('status').count()) === 1);
check('the input still carries the file (it is what gets posted)', (await p.locator('#photo').evaluate((el) => el.files.length)) === 1);

await p.setInputFiles('#photo', { name: 'second.png', mimeType: 'image/png', buffer: await png(p, 800, 1600) });
await p.waitForFunction(() => document.querySelector('.wayfinder-file-preview__caption')?.textContent.includes('second.png') && document.querySelector('.wayfinder-file-preview__image')?.naturalHeight === 640, null, { timeout: 8000 }).catch(() => {});
const second = await p.locator('.wayfinder-file-preview__image').evaluate((img) => ({ w: img.naturalWidth, h: img.naturalHeight }));
check('choosing another file replaces the picture (a portrait one is limited by height)', second.h === 640 && second.w === 320, `${second.w}x${second.h}`);

await p.setInputFiles('#photo', []);
check('clearing the choice hides the preview', await p.locator('.wayfinder-file-preview').isHidden());
await p.context().close();

// A file the browser cannot decode: no picture, a caption that says so, and the file is still chosen.
p = await open('/');
await p.setInputFiles('#photo', { name: 'broken.jpg', mimeType: 'image/jpeg', buffer: Buffer.from('this is not an image') });
await p.waitForFunction(() => /not available/.test(document.querySelector('.wayfinder-file-preview__caption')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
check('a file that cannot be decoded gets no picture and an honest caption', (await p.locator('.wayfinder-file-preview__image').isHidden()) && /not available/.test(await p.locator('.wayfinder-file-preview__caption').innerText()), await p.locator('.wayfinder-file-preview__caption').innerText());
check('and the file is still chosen', (await p.locator('#photo').evaluate((el) => el.files.length)) === 1);
await p.context().close();

// An input that is not marked is left alone.
p = await open('/unmarked');
await p.setInputFiles('#photo', { name: 'x.png', mimeType: 'image/png', buffer: image });
await p.waitForTimeout(300);
check('an unmarked input gets no preview', (await p.locator('.wayfinder-file-preview').count()) === 0);
await p.context().close();

check('no page, console or Content-Security-Policy errors', errors.length === 0, errors.join(' | '));
await browser.close(); server.close();
console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
