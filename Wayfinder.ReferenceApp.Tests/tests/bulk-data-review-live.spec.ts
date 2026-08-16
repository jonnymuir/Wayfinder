import { test, expect } from '@playwright/test';
import { LiveAppHost } from './support/live-app-host';
import { DEMO_USERS, loginAs } from './fixtures';

// Real cross-process integration for docs/guides/bulk-data-review.md's own worked example
// (Wayfinder.ReferenceApp/service-blueprints/njf-contributions.json): proves a caseworker can
// upload a real CSV, have it genuinely validated by SafetyNetUnderwriting's own
// ContributionsValidation.cs (a separately-running app, not a stub), correct only the row that's
// flagged via the client-fetched bulk-data-review card UI, and resubmit — without the "Accept
// and finish" route ever becoming reachable until the response genuinely has zero errors. Needs
// its own AppHost lifecycle (see live-app-host.ts) — run with `npm run test:playwright:live`.
const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';

const appHost = new LiveAppHost();

// Row 1 is clean. Row 2 has a genuine SafetyNet Underwriting error (an unrecognised tier) — real
// server-side validation on a real second app, not a scripted fixture.
const contributionsCsv = [
  'memberRef,memberName,tier,fireEndorsement,under18,dob,monthlyContribution',
  'NJF-001,Alice,Recreational,N,N,,15.00',
  'NJF-002,Bob,Bogus,N,N,,15.00'
].join('\n');

test.describe('Bulk data review: real cross-process round trip', () => {
  test.describe.configure({ mode: 'serial' });
  test.setTimeout(3 * 60_000);

  test.beforeAll(async () => {
    await appHost.start();
  });

  test.afterAll(async () => {
    await appHost.stop();
  });

  test.beforeEach(async ({ request }) => {
    await request.delete(`${REFERENCE_APP}/api/test/reset`);
    await request.delete(`${SAFETYNET}/api/test/reset`);
  });

  test('caseworker uploads a contributions file, corrects the one flagged row via the card UI, resubmits, and accepts', async ({
    browser
  }) => {
    const context = await browser.newContext({ baseURL: REFERENCE_APP });
    const page = await context.newPage();
    await loginAs(page, DEMO_USERS.caseworker);

    await page.goto('/caseworker/njf-contributions/new');
    await expect(page.getByRole('heading', { name: 'Submit contributions file' })).toBeVisible();

    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(contributionsCsv)
    });
    await page.getByRole('button', { name: 'Submit' }).click();

    // PRG: advancing through a Split gateway redirects to the caseworker's own queue list — same
    // convention the existing risk-assessment flow already uses (support-systems-live.spec.ts).
    // The submission must stay reachable, flagged "Waiting", not vanish while it's out with
    // SafetyNet Underwriting.
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    const queueRow = page.locator('tr', { hasText: 'Submit an NJF contributions file' });
    await expect(queueRow.getByText('Waiting')).toBeVisible();
    await queueRow.getByRole('link', { name: 'View' }).click();

    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();

    // Real batch processing on a genuinely separate app, with a real artificial delay
    // (SafetyNetUnderwriting/Program.cs) — the review stage's own bulk-dataset-ingest action only
    // fires once the join actually releases, not on the first poll.
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    // The bulk-data-review card UI is entirely client-fetched (wayfinder-bulk-data-review.js) —
    // wait for the real content, not the server-rendered "Loading…" skeleton it replaces.
    const attentionCard = page.locator('.wayfinder-bulk-review__card', { hasText: 'NJF-002' });
    await expect(attentionCard).toBeVisible({ timeout: 10_000 });
    await expect(attentionCard.getByText(/Unrecognised tier/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Accept and finish' })).toHaveCount(0);

    await attentionCard.getByLabel('Membership tier').fill('Recreational');
    await attentionCard.getByRole('button', { name: 'Save correction' }).click();
    await expect(attentionCard.getByText('Saved')).toBeVisible();

    // A genuine loop: this re-fires the same Split gateway, materializing the just-corrected
    // dataset (not the original upload) back to SafetyNet Underwriting for real revalidation —
    // same PRG redirect back to the queue list as the initial submit.
    await page.getByRole('button', { name: 'Resubmit corrected file' }).click();
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await expect(queueRow.getByText('Waiting')).toBeVisible();
    await queueRow.getByRole('link', { name: 'View' }).click();
    const instanceUrl = page.url();

    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole('button', { name: 'Accept and finish' })).toBeVisible({ timeout: 10_000 });

    await page.getByRole('button', { name: 'Accept and finish' }).click();

    // "Done" is a terminal confirmation stage — same PRG redirect to the queue list as every
    // other advance, but this time the instance has nothing left to act on, so it drops off the
    // list entirely rather than showing "Waiting". Go straight back to the instance's own URL
    // (captured above) to see the actual confirmation page.
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await page.goto(instanceUrl);
    await expect(page.getByRole('heading', { name: 'Contributions file accepted' })).toBeVisible();

    await context.close();
  });
});
