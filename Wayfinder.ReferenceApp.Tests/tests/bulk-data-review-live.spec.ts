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

  test('NJF operations uploads a contributions file, corrects the one flagged row via the card UI, resubmits, and accepts', async ({
    browser
  }) => {
    const context = await browser.newContext({ baseURL: REFERENCE_APP });
    const page = await context.newPage();
    await loginAs(page, DEMO_USERS.njfOperations);

    await page.goto('/caseworker/njf-contributions/new');
    await expect(page.getByRole('heading', { name: 'Submit contributions file' })).toBeVisible();

    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(contributionsCsv)
    });
    await page.getByRole('button', { name: 'Submit' }).click();

    // PRG, but the caseworker's own cursor is now parked at the automation Join gateway, and
    // lands there DIRECTLY — the same position the citizen's own post-review join has always put
    // people on straight away, rather than forcing a detour through the queue list first just to
    // click back in (ResponseState "defer" counts as "more to do here" in Program.cs's
    // post-advance redirect — see its own comment for why).
    const instanceUrl = page.url();
    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();

    // The submission must also STAY REACHABLE from the caseworker's own worklist while it's out
    // with SafetyNet Underwriting, flagged "Waiting" — for anyone who navigates away and comes
    // back later, not just the person who just submitted it.
    await page.goto('/caseworker/queue');
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

    // No "Save correction" button — a correction autosaves (debounced) once you stop typing, so
    // a second edit made right after the first can never be silently left out of "Resubmit
    // corrected file" the way a forgotten manual save used to allow.
    await attentionCard.getByLabel('Membership tier').fill('Recreational');
    await expect(attentionCard.getByText('Saved')).toBeVisible();

    // A genuine loop: this re-fires the same Split gateway, materializing the just-corrected
    // dataset (not the original upload) back to SafetyNet Underwriting for real revalidation —
    // landing directly back on this same instance's own wait screen, same as the initial submit.
    // List-visibility while waiting is already covered above; no need to prove it twice.
    await page.getByRole('button', { name: 'Resubmit corrected file' }).click();
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
