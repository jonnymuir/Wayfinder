import { test, expect, type Browser, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { LiveAppHost } from '../support/live-app-host';
import { beat, clearBeat, showSlate, clearSlate, moveNarrationTo, startNarrationTimeline, getNarrationTimeline } from './support/narration';
import { humanClick, humanType, humanMoveTo } from './support/human-interactions';

/**
 * A narrated, single-take walkthrough of bulk data review (see docs/guides/bulk-data-review.md
 * and docs/demos/bulk-data-review-walkthrough.md, the written companion this recording mirrors):
 * Wayfinder's first bulk/row-level data capability, told through the National Juggling
 * Federation submitting a monthly contributions file to SafetyNet Underwriting — the same
 * fictional insurer from the overview demo, now doing automatic row-by-row validation instead of
 * a human decision. Shows the whole loop: upload, a genuinely separate app validating for real,
 * a client-fetched card UI surfacing only the rows that need attention, an inline correction,
 * a resubmission that materializes the corrected dataset (not the original upload), and a route
 * that only becomes reachable once the data is genuinely clean — plus a warning that does NOT
 * block finishing, to show that distinction is real, not just a label.
 *
 * Not a CI test: this is a recording tool (see playwright.demo.config.ts, which no CI script
 * references). Every beat narrates something a real assertion just checked.
 */

const REFERENCE_APP = 'https://localhost:7286';
const OUTPUT_DIR = path.resolve(process.cwd(), '../docs/demos');
const OUTPUT_FILE = path.join(OUTPUT_DIR, 'bulk-data-review.webm');

const appHost = new LiveAppHost();
let browserRef: Browser;
let page: Page;

// Same file a human following docs/demos/bulk-data-review-walkthrough.md would download and
// upload by hand — one source of truth, so the recording can never drift from the sample.
// Five members: three clean, one with a real tier error, one with a contribution genuinely
// outside SafetyNet Underwriting's expected band for its tier (a warning, not an error — see
// ContributionsValidation.cs). Small enough to narrate every row; large enough that "only two
// rows need attention" is a real, visible saving, not a triviality.
const ORIGINAL_CSV = readFileSync(
  path.resolve(process.cwd(), '../docs/demos/samples/njf-contributions-sample.csv'),
  'utf-8'
).trimEnd();

test.describe('Bulk data review — narrated end-to-end demo', () => {
  test.describe.configure({ mode: 'serial' });
  test.setTimeout(10 * 60_000);

  test.beforeAll(async ({ browser }) => {
    browserRef = browser;
    await appHost.start();

    const context = await browserRef.newContext({
      baseURL: REFERENCE_APP,
      ignoreHTTPSErrors: true,
      viewport: { width: 1440, height: 900 },
      recordVideo: { dir: path.join(process.cwd(), 'test-results', 'demo-video'), size: { width: 1440, height: 900 } }
    });
    page = await context.newPage();
    startNarrationTimeline();

    await page.request.delete(`${REFERENCE_APP}/api/test/reset`);
  });

  test.afterAll(async () => {
    const video = page.video();
    const context = page.context();
    await page.close();
    if (video) {
      await mkdir(OUTPUT_DIR, { recursive: true });
      await video.saveAs(OUTPUT_FILE);
      await video.delete();
      console.log(`\nDemo video: ${OUTPUT_FILE}`);
      console.log('Narration timeline:');
      for (const entry of getNarrationTimeline()) {
        const seconds = (entry.atMs / 1000).toFixed(1).padStart(6);
        console.log(`  ${seconds}s  [${entry.kind}] ${entry.text}`);
      }
    }
    await context.close();
    await appHost.stop();
  });

  test('Act 1 — NJF operations submits the monthly contributions file', async () => {
    await page.goto('/account/login');

    await showSlate(page, {
      eyebrow: 'WAYFINDER — BULK DATA REVIEW',
      title: 'When the answer isn’t a decision, it’s a spreadsheet',
      body:
        'The National Juggling Federation arranges group insurance for its members. Every month it sends ' +
        'SafetyNet Underwriting a CSV of contributions; SafetyNet sends the same file back with a matched ' +
        'member ID and an error or warning on any row that needs attention. The old way: open it in Excel, ' +
        'hunt for the flagged rows by eye, fix them, re-upload the whole thing. This film shows what ' +
        'Wayfinder does instead.'
    });
    await clearSlate(page);

    await beat(page, 'setup', 'Meet Priya Shah, NJF operations — not the licensing caseworker from the other film. Same backstage tool, a different job.');
    await humanType(page, page.getByLabel('Email address'), 'njf-operations@example.test');
    await humanType(page, page.locator('#password'), 'wayfinder-demo');
    await humanClick(page, page.getByRole('button', { name: 'Sign in' }));

    // Signing in as a caseworker-role user lands straight on the shared worklist (/caseworker/queue),
    // not the home page — the "Submit an NJF contributions file" link lives on the latter.
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await humanClick(page, page.getByRole('link', { name: 'Wayfinder Reference App' }));
    await humanClick(page, page.getByRole('link', { name: 'Submit an NJF contributions file' }));
    await expect(page.getByRole('heading', { name: 'Submit contributions file' })).toBeVisible();
    await beat(page, 'setup', 'Five members this cycle. Two of them, it turns out, need attention — one genuine error, one warning.');

    await beat(page, 'intent', 'Priya uploads the file exactly as her membership system exported it, and submits.');
    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(ORIGINAL_CSV)
    });
    await humanClick(page, page.getByRole('button', { name: 'Submit' }));

    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    const queueRow = page.locator('tr', { hasText: 'Submit an NJF contributions file' });
    await expect(queueRow.getByText('Waiting')).toBeVisible();
    await beat(page, 'recap', 'Straight back to the worklist, tagged “Waiting” — the same pattern the licensing demo uses: sent off to another system, but never lost from view.');

    await humanClick(page, queueRow.getByRole('link', { name: 'View' }));
    // SafetyNet Underwriting's own artificial processing delay is only ~3 seconds — by the time
    // the narration beats above have played, it may have already resolved. Either outcome is a
    // true statement about the app; narrate whichever one actually happened rather than assuming
    // a fixed timing. Act 2's own wait for the review heading handles both cases either way (the
    // wait screen's poll script reloads on its own if it's still showing).
    const stillProcessing = await page.getByText('SafetyNet Underwriting is processing the contributions file.')
      .waitFor({ state: 'visible', timeout: 3_000 })
      .then(() => true)
      .catch(() => false);
    await beat(page, 'note', stillProcessing
      ? 'SafetyNet Underwriting is a genuinely separate app, on its own port, applying its own underwriting rules — not a stub.'
      : 'SafetyNet Underwriting — a genuinely separate app, on its own port — has already answered.');
  });

  test('Act 2 — only the rows that need attention, fetched, not the whole file', async () => {
    await beat(page, 'intent', 'A few seconds of real processing, then the review stage — as soon as the response is ready, not before.');
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    const summary = page.locator('.wayfinder-stat-group');
    await expect(summary).toBeVisible();
    await beat(page, 'setup', 'Five rows in, five back — three accepted outright, one error, one warning. Wayfinder never re-derives this; it’s exactly what SafetyNet Underwriting reported.');

    const errorCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' });
    await expect(errorCard).toBeVisible({ timeout: 10_000 });
    await errorCard.scrollIntoViewIfNeeded();
    await beat(page, 'recap', 'This card was fetched by the browser, on its own, after the page loaded — and it’s the only row like it on screen. In a real file of two thousand rows, the other 1,998 clean ones are never sent here at all.');

    const warningCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-004' });
    await expect(warningCard).toBeVisible();
    await warningCard.scrollIntoViewIfNeeded();
    await expect(warningCard.getByText(/outside the expected/)).toBeVisible();
    await beat(page, 'note', 'Red for an error, amber for a warning — and that distinction is about to matter.');

    await expect(page.getByRole('button', { name: 'Accept and finish' })).toHaveCount(0);
    await beat(page, 'recap', 'No “Accept and finish” button anywhere on this page. One row still has a genuine error — that route simply isn’t offered yet, the same declarative shape as every other rule in Wayfinder.');
  });

  test('Act 3 — correcting a row is a small request, not a page reload', async () => {
    const errorCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' });
    await errorCard.scrollIntoViewIfNeeded();
    await expect(errorCard.getByText(/Unrecognised tier/)).toBeVisible();
    await beat(page, 'intent', '“Bogus” isn’t a real membership tier — a genuine data problem in Cara Delgado’s row. Priya fixes it in place.');

    await humanType(page, errorCard.getByLabel('Membership tier'), 'Recreational');
    await humanClick(page, errorCard.getByRole('button', { name: 'Save correction' }));
    await expect(errorCard.getByText('Saved')).toBeVisible();
    await beat(page, 'recap', 'Saved — a small POST to this row alone. The other four rows, and the file itself, were never touched.');

    await beat(page, 'intent', 'Resubmitting sends the whole file back — SafetyNet Underwriting’s contract never changes — but built from the corrected data, not Priya’s original upload.');
    await humanClick(page, page.getByRole('button', { name: 'Resubmit corrected file' }));

    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    const queueRow = page.locator('tr', { hasText: 'Submit an NJF contributions file' });
    await expect(queueRow.getByText('Waiting')).toBeVisible();
    await humanClick(page, queueRow.getByRole('link', { name: 'View' }));
    // Same race as Act 1 — narrate whichever is genuinely true; Act 4's own wait for the review
    // heading covers both outcomes.
    const stillProcessingAgain = await page.getByText('SafetyNet Underwriting is processing the contributions file.')
      .waitFor({ state: 'visible', timeout: 3_000 })
      .then(() => true)
      .catch(() => false);
    await beat(page, 'note', stillProcessingAgain
      ? 'A real loop through the same two systems — not a special-cased “try again” path.'
      : 'Already answered again — the same real loop through both systems, just faster this time.');
  });

  test('Act 4 — clean enough to finish, even with a warning still open', async () => {
    await beat(page, 'intent', 'SafetyNet Underwriting genuinely re-checks the corrected file this time — nothing here is cached from the first pass.');
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    await expect(page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' })).toHaveCount(0);
    await beat(page, 'recap', 'Cara Delgado’s row is gone — no error left to show.');

    const warningCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-004' });
    await expect(warningCard).toBeVisible();
    await warningCard.scrollIntoViewIfNeeded();
    await beat(page, 'setup', 'Dev Patel’s contribution is still flagged — SafetyNet Underwriting isn’t wrong to keep raising it. But a warning was never what blocked finishing.');

    const acceptButton = page.getByRole('button', { name: 'Accept and finish' });
    await expect(acceptButton).toBeVisible({ timeout: 10_000 });
    await moveNarrationTo(page, 'top');
    await beat(page, 'recap', '“Accept and finish” is offered the moment the error count reaches zero — one declarative rule, checked against real data from a real second system, not a hand-written condition buried in a controller.', { position: 'top', holdMs: 5_500 });

    // PRG, same as every other advance — and "done" is a terminal confirmation stage, so the
    // instance drops off the queue list entirely rather than showing "Waiting" (see
    // bulk-data-review-live.spec.ts's own remarks on the identical case). Capture the direct URL
    // before clicking so there's somewhere to go back to.
    const instanceUrl = page.url();
    await humanClick(page, acceptButton);
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await page.goto(instanceUrl);
    await expect(page.getByRole('heading', { name: 'Contributions file accepted' })).toBeVisible();
    await beat(page, 'recap', 'Done — with an outstanding warning still on record, exactly as it should be: a nudge, not a blocker.');

    await showSlate(page, {
      eyebrow: 'WHAT YOU JUST WATCHED',
      title: 'Bulk data the same way Wayfinder already treats everything else',
      body:
        'A file exchange with a system that only ever speaks whole-file-in, whole-file-out — hidden behind a ' +
        'modern review experience that only shows what needs attention, corrects it in place, and resubmits ' +
        'the corrected dataset, not the original upload. Errors block; warnings inform. Every rule declared, ' +
        'not hand-coded — the same model as the rest of Wayfinder, now handling rows by the thousand instead ' +
        'of one case at a time.',
      holdMs: 8_000
    });
    await clearSlate(page);
  });
});
