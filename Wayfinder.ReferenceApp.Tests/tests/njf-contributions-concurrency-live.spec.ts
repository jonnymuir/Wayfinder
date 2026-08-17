import { test, expect } from '@playwright/test';
import { LiveAppHost } from './support/live-app-host';
import { DEMO_USERS, loginAs } from './fixtures';

// Real cross-process coverage for ProcessManagerEngine.GetCurrentOrStartFresh, applied to
// njf-contributions' "Submit an NJF contributions file" link (Wayfinder.ReferenceApp/Program.cs).
// That link is a distinct "start a new one" affordance, not "continue where I left off" — a
// non-terminal in-progress submission must be reinstated (never abandoned), but a genuinely
// terminal one must not be returned forever the way plain ambient GetCurrent used to. Needs its
// own AppHost lifecycle (see live-app-host.ts) — run with `npm run test:playwright:live`.
const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';

const appHost = new LiveAppHost();

// No errors, no warnings — reaches "Contributions file accepted" directly (no confirm-before-
// finishing detour), the simplest way to genuinely reach terminal for this test's purposes.
const cleanCsv = [
  'memberRef,memberName,tier,fireEndorsement,under18,dob,monthlyContribution',
  'NJF-001,Alice,Recreational,N,N,,15.00'
].join('\n');

test.describe('NJF contributions: terminal-aware "start a new one"', () => {
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

  test('a non-terminal in-progress submission is reinstated, not abandoned, by clicking "Submit an NJF contributions file" again', async ({
    browser
  }) => {
    const context = await browser.newContext({ baseURL: REFERENCE_APP });
    const page = await context.newPage();
    await loginAs(page, DEMO_USERS.njfOperations);

    await page.goto('/caseworker/njf-contributions/new');
    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(cleanCsv)
    });
    await page.getByRole('button', { name: 'Submit' }).click();
    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();
    const inProgressUrl = page.url();

    // The same link, clicked again while that submission is still out with SafetyNet — must
    // reinstate it, not abandon it for a second, blank one.
    await page.goto('/caseworker/njf-contributions/new');

    expect(page.url()).toBe(inProgressUrl);
    await expect(page.getByText('SafetyNet Underwriting is processing the contributions file.')).toBeVisible();

    await context.close();
  });

  test('a terminal (already-accepted) submission does not block starting a genuinely fresh one', async ({
    browser
  }) => {
    const context = await browser.newContext({ baseURL: REFERENCE_APP });
    const page = await context.newPage();
    await loginAs(page, DEMO_USERS.njfOperations);

    await page.goto('/caseworker/njf-contributions/new');
    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(cleanCsv)
    });
    await page.getByRole('button', { name: 'Submit' }).click();
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 20_000 });

    // PRG: "done" is a terminal confirmation stage, so advancing into it redirects to the queue
    // list rather than rendering it directly (same pattern bulk-data-review-live.spec.ts already
    // covers) — capture the instance's own URL before clicking, then navigate back to it.
    const acceptedInstanceUrl = page.url();
    const acceptedInstanceId = acceptedInstanceUrl.split('/').pop()!;
    await page.getByRole('button', { name: 'Accept and finish' }).click();
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await page.goto(acceptedInstanceUrl);
    await expect(page.getByRole('heading', { name: 'Contributions file accepted' })).toBeVisible();

    // The worklist's own "Done" status filter (see docs/guides/queue-worklist-filtering.md) is
    // what actually closes the visibility gap this whole feature exists for: njf-contributions
    // has no separate citizen frontstage, so this operations worklist is the ONLY place a
    // completed submission was ever going to be discoverable other than a remembered URL.
    await page.goto('/caseworker/queue');
    await expect(page.getByText('No applications match the current filters')).toBeVisible();

    await page.getByLabel('Done').check();
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByText('No applications match the current filters')).not.toBeVisible();
    await expect(page.getByRole('table').getByText('Done')).toBeVisible();
    await expect(page.getByRole('link', { name: 'View' })).toBeVisible();

    // Free-text search against the same instance id fragment the worklist row itself displays.
    await page.getByLabel('Search').fill(acceptedInstanceId.slice(0, 8));
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByRole('link', { name: 'View' })).toBeVisible();

    await page.getByLabel('Search').fill('no-such-instance-id-fragment');
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page.getByText('No applications match the current filters')).toBeVisible();

    // Before this fix: the exact same link kept returning this same terminal instance forever —
    // "Submit an NJF contributions file" could never actually be used a second time. This is the
    // whole point of GetCurrentOrStartFresh.
    await page.goto('/caseworker/njf-contributions/new');

    await expect(page.getByRole('heading', { name: 'Submit contributions file' })).toBeVisible();
    expect(page.url()).not.toBe(acceptedInstanceUrl);

    // The accepted instance still genuinely exists — reachable both by its own URL and (proven
    // above) via the worklist's own "Done" filter.
    await page.goto(acceptedInstanceUrl);
    await expect(page.getByRole('heading', { name: 'Contributions file accepted' })).toBeVisible();

    await context.close();
  });
});
