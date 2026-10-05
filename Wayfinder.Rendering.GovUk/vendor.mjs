// Vendors the real govuk-frontend build (the version pinned in package.json) into wwwroot/govuk-frontend.
// Run `npm ci && npm run vendor` after changing that version.
//
// govuk-frontend's pre-built CSS requests its fonts and images at an absolute "/assets/...", which only
// resolves if the host re-roots that path. This package is served from /_content/Wayfinder.Rendering.GovUk/,
// so the URLs are rewritten to be relative to the CSS file itself: the assets then resolve wherever the
// package is served from, with nothing for a host to configure.
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, 'node_modules', 'govuk-frontend', 'dist', 'govuk');
const target = join(here, 'wwwroot', 'govuk-frontend');

rmSync(join(target, 'assets'), { recursive: true, force: true });
mkdirSync(join(target, 'assets', 'images'), { recursive: true });
cpSync(join(source, 'assets', 'fonts'), join(target, 'assets', 'fonts'), { recursive: true });
// Only what the CSS itself references; the favicon/app-icon set is for a host's own <head>.
cpSync(join(source, 'assets', 'images', 'govuk-crest.svg'), join(target, 'assets', 'images', 'govuk-crest.svg'));
cpSync(join(source, 'govuk-frontend.min.js'), join(target, 'govuk-frontend.min.js'));

const css = readFileSync(join(source, 'govuk-frontend.min.css'), 'utf8').replaceAll('url(/assets/', 'url(assets/');
writeFileSync(join(target, 'govuk-frontend.min.css'), css);
