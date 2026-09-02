import { test, expect, type Browser, type Locator, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { LiveAppHost } from './support/live-app-host';
import { DEMO_USERS, loginAs, captureDocScreenshot } from './fixtures';

/**
 * Regenerates the still screenshots embedded in docs/demos/bulk-data-review-walkthrough.md.
 *
 * Not a CI assertion in the usual sense — it's a capture tool, the bulk-data-review equivalent of
 * `npm run docs:screenshots` (see fixtures.ts's captureDocScreenshot and calculations-editor.spec.ts).
 * It's skipped entirely unless CAPTURE_DOC_SCREENSHOTS is set, so the live suite's normal runs
 * never boot a second AppHost or rewrite committed images. When it does run, every screenshot is
 * still taken as the side effect of a real assertion that just passed on the same screen — the
 * walkthrough can't show a state the app didn't actually produce.
 *
 * Run it with:
 *   cd Wayfinder.ReferenceApp.Tests && npm run docs:screenshots:bulk-data-review
 *
 * It owns the whole Wayfinder.AppHost lifecycle (Wayfinder.ReferenceApp + SafetyNetUnderwriting,
 * via Aspire — see live-app-host.ts), so nothing may already be listening on their ports when it
 * starts. The service-blueprint editor screens (Act 5) additionally need Wayfinder.Editor.Client's
 * compiled bundle on disk (`npm run build` in ../Wayfinder.Editor.Client).
 */

const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';
const SHOTS = 'docs/demos/screenshots/bulk-data-review';
const CAPTURING = Boolean(process.env.CAPTURE_DOC_SCREENSHOTS);

// The exact file a human following the walkthrough would download and upload by hand — one
// source of truth, shared with bulk-data-review-demo.spec.ts. Five members: three clean, Cara
// Delgado (NJF-003) with a genuine tier error, Dev Patel (NJF-004) with a contribution outside
// SafetyNet Underwriting's expected band for its tier (a warning, not an error).
const ORIGINAL_CSV = readFileSync(
  path.resolve(process.cwd(), '..', 'docs/demos/samples/njf-contributions-sample.csv'),
  'utf-8'
).trimEnd();

const appHost = new LiveAppHost();
let browserRef: Browser;
let page: Page;
let instanceUrl = '';

test.describe('Bulk data review — walkthrough screenshot capture', () => {
  test.describe.configure({ mode: 'serial' });
  test.setTimeout(5 * 60_000);
  test.skip(!CAPTURING, 'Screenshot capture only — set CAPTURE_DOC_SCREENSHOTS=1 to run.');

  test.beforeAll(async ({ browser }) => {
    browserRef = browser;
    await appHost.start();

    const context = await browserRef.newContext({
      baseURL: REFERENCE_APP,
      ignoreHTTPSErrors: true,
      viewport: { width: 1440, height: 900 }
    });
    page = await context.newPage();

    await page.request.delete(`${REFERENCE_APP}/api/test/reset`);
    await page.request.delete(`${SAFETYNET}/api/test/reset`);
  });

  test.afterAll(async () => {
    await page?.context().close();
    await appHost.stop();
  });

  const shot = (target: Page | Locator, name: string) => captureDocScreenshot(target, `${SHOTS}/${name}`);

  test('Act 1 — submitting the file', async () => {
    await loginAs(page, DEMO_USERS.njfOperations);
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();

    await page.goto('/caseworker/njf-contributions/new');
    await expect(page.getByRole('heading', { name: 'Submit contributions file' })).toBeVisible();
    await expect(page.getByLabel('Contributions file')).toBeVisible();
    await shot(page, '01-submit-contributions-file.png');

    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(ORIGINAL_CSV)
    });
    await page.getByRole('button', { name: 'Submit' }).click();

    // Lands directly on this instance's own wait screen — no detour through the queue list first.
    instanceUrl = page.url();
    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();
    await shot(page, '02-wait-screen.png');

    // And it stays reachable, tagged "Waiting", from the shared worklist while it's out with
    // SafetyNet Underwriting.
    await page.goto('/caseworker/queue');
    const queueRow = page.locator('tr', { hasText: 'Submit an NJF contributions file' });
    await expect(queueRow.getByText('Waiting')).toBeVisible();
    await shot(page, '03-worklist-waiting.png');
  });

  test('Act 2 — only the rows that need attention', async () => {
    await page.goto(instanceUrl);
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    const errorCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' });
    const warningCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-004' });
    await expect(errorCard).toBeVisible({ timeout: 10_000 });
    await expect(warningCard).toBeVisible();

    // Summary stat group: 1 error, 1 warning, 3 accepted — exactly what SafetyNet reported.
    await expect(page.locator('.wayfinder-stat-group')).toBeVisible();
    await page.evaluate(() => window.scrollTo(0, 0));
    await shot(page, '04-review-summary.png');

    await expect(errorCard.getByText(/Unrecognised tier 'Bogus'/)).toBeVisible();
    await errorCard.evaluate(el => el.scrollIntoView({ block: 'center' }));
    await page.waitForTimeout(300);
    await shot(errorCard, '05-error-card.png');

    await expect(warningCard.getByText(/outside the expected .* band for Performer/)).toBeVisible();
    await warningCard.evaluate(el => el.scrollIntoView({ block: 'center' }));
    await page.waitForTimeout(300);
    await shot(warningCard, '06-warning-card.png');

    // One row still has a genuine error, so the "Accept and finish" route simply isn't offered.
    await expect(page.getByRole('button', { name: 'Accept and finish' })).toHaveCount(0);
  });

  test('Act 3 — correcting a row autosaves', async () => {
    const errorCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' });
    await errorCard.scrollIntoViewIfNeeded();
    await errorCard.getByLabel('Membership tier').fill('Recreational');

    // The correction autosaves once she stops typing — no "Save" button. The card's own status
    // line settles on the per-service "pending resubmission" wording (nothing here revalidates a
    // correction — only resubmitting through SafetyNet Underwriting does).
    await expect(errorCard.getByText('Pending resubmission')).toBeVisible({ timeout: 5_000 });
    await errorCard.evaluate(el => el.scrollIntoView({ block: 'center' }));
    await page.waitForTimeout(300);
    await shot(errorCard, '07-correction-autosaved.png');

    await page.getByRole('button', { name: 'Resubmit corrected file' }).click();
    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();
  });

  test('Act 4 — a warning still needs an explicit yes', async () => {
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    // Cara Delgado's row is gone; Dev Patel's warning remains; "Accept and finish" now appears.
    await expect(page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-003' })).toHaveCount(0);
    const warningCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-004' });
    await expect(warningCard).toBeVisible();
    const acceptButton = page.getByRole('button', { name: 'Accept and finish' });
    await expect(acceptButton).toBeVisible({ timeout: 10_000 });
    // Scroll the flagged row and the now-available "Accept and finish" route into one frame.
    await acceptButton.evaluate(el => el.scrollIntoView({ block: 'end' }));
    await page.waitForTimeout(300);
    await shot(page, '08-review-after-resubmit.png');

    await acceptButton.click();
    await expect(page.getByRole('heading', { name: 'Confirm before finishing' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Yes, accept with warnings' })).toBeVisible();
    await shot(page, '09-confirm-before-finishing.png');

    const confirmUrl = page.url();
    await page.getByRole('button', { name: 'Yes, accept with warnings' }).click();
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();

    await page.goto(confirmUrl);
    await expect(page.getByRole('heading', { name: 'Contributions file accepted' })).toBeVisible();
    await shot(page, '10-file-accepted.png');
  });

  test('Act 5 — the blueprint that declares all of it', async () => {
    await page.goto('/service-blueprint-editor');
    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await expect(shell).toBeVisible({ timeout: 15_000 });

    await page.locator('select.service-blueprint-selector')
      .selectOption({ label: 'Submit an NJF contributions file (njf-contributions)' });
    await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'njf-contributions', { timeout: 15_000 });
    await page.waitForTimeout(400);

    // Blocking capability errors are fixed (see ReferenceActors.NjfTeamComponentTypes) — the
    // rail's only remarks now are the advisory "service-sourced field" warnings, which don't
    // block Save. Guard that so this screenshot can't silently go back to showing a broken blueprint.
    const validationBanner = page.getByText(/need attention/).first();
    if (await validationBanner.count()) {
      await expect(validationBanner).not.toContainText(/error/i);
    }

    await page.getByRole('button', { name: 'Fit to screen' }).click();
    await page.waitForTimeout(400);
    // Fit-to-screen bottoms out at ~40% for this tall, narrow graph — zoom back in, centred on
    // the flow, so the nodes are actually readable.
    await page.mouse.move(740, 470);
    for (let i = 0; i < 3; i++) {
      await page.mouse.wheel(0, -120);
      await page.waitForTimeout(90);
    }
    await page.waitForTimeout(300);
    await shot(page, '11-editor-canvas.png');

    // Zoom in on the review stage and its two same-labelled "Accept and finish" routes.
    const reviewNode = page.getByRole('button', { name: /Review contributions file/ }).first();
    await expect(reviewNode).toBeVisible();
    const reviewBox = await reviewNode.boundingBox();
    if (reviewBox) {
      await page.mouse.move(reviewBox.x + reviewBox.width / 2, reviewBox.y + reviewBox.height / 2);
    }
    for (let i = 0; i < 4; i++) {
      await page.mouse.wheel(0, -120);
      await page.waitForTimeout(90);
    }
    await page.waitForTimeout(400);
    await shot(page, '12-editor-review-routes.png');

    // Select the review stage, scroll its properties panel to the bulk-dataset-ingest column schema.
    await reviewNode.click();
    const inspector = page.locator('wayfinder-step-inspector');
    await expect(inspector.locator('#stage-actions-heading')).toBeVisible();
    await inspector.locator('#stage-actions-heading').scrollIntoViewIfNeeded();
    await page.mouse.move(1050, 500);
    for (let i = 0; i < 8; i++) {
      await page.mouse.wheel(0, 260);
      await page.waitForTimeout(140);
    }
    await expect(inspector.getByLabel('Column key').first()).toBeVisible({ timeout: 5_000 });
    await shot(page, '13-editor-columns-properties.png');
  });
});
