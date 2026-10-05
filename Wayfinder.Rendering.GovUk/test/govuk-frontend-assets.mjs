// The vendored govuk-frontend CSS must be self-contained: every url() in it resolves, relative to the CSS
// file, to a file this package ships. An absolute "/assets/..." URL would only work if each host re-rooted
// that path (and 404s the fonts when it doesn't), so it is a failure here.
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'wwwroot', 'govuk-frontend');
const css = readFileSync(join(root, 'govuk-frontend.min.css'), 'utf8');
const urls = [...css.matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)/g)].map((m) => m[1]).filter((u) => !u.startsWith('data:'));

let failures = 0;
const fail = (message) => {
  failures++;
  console.error(`FAIL  ${message}`);
};

if (urls.length === 0) fail('found no url() references to check; the pattern no longer matches the CSS');
for (const url of new Set(urls)) {
  if (/^([a-z]+:)?\/\//i.test(url) || url.startsWith('/')) fail(`${url} is absolute, so it only resolves if the host re-roots it`);
  else if (!existsSync(join(root, url))) fail(`${url} is not shipped under wwwroot/govuk-frontend`);
  else console.log(`  ok  ${url}`);
}

if (failures > 0) process.exit(1);
