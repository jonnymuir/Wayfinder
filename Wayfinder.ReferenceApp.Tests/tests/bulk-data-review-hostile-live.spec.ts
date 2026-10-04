import { test, expect } from '@playwright/test';
import { LiveAppHost } from './support/live-app-host';
import { DEMO_USERS, loginAs } from './fixtures';

// Hostile data through the real bulk-data review. The cells of an uploaded dataset are attacker-
// controlled and are rendered client-side (wayfinder-bulk-data-review.js) into HTML attributes and
// text; an escaper that missed quotes once let a cell value break out of value="…" and inject an
// event handler. Neither CodeQL (it does not treat fetch() data as untrusted) nor ZAP's passive
// baseline (it sends nothing) can see that, so this uploads a poisoned file through the real
// cross-process flow (ReferenceApp + SafetyNetUnderwriting) and checks the card UI renders it inert.
// Needs its own AppHost lifecycle, like bulk-data-review-live.spec.ts: `npm run test:playwright:live`.
const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';

const appHost = new LiveAppHost();

const HOSTILE_ROW_KEY = 'NJF-9"><img src=x onerror="window.__pwned=1">';
const HOSTILE_NAME = 'x" autofocus onfocus="window.__pwned=1" data-evil="1';

// CSV quoting: a field with quotes is wrapped in quotes and each inner quote is doubled. The bogus tier
// makes SafetyNet flag the row, so it lands under "Needs attention" without any extra clicks.
const csvField = (value: string) => `"${value.replace(/"/g, '""')}"`;
const hostileCsv = [
  'memberRef,memberName,tier,fireEndorsement,under18,dob,monthlyContribution',
  [csvField(HOSTILE_ROW_KEY), csvField(HOSTILE_NAME), 'Bogus', 'N', 'N', '', '15.00'].join(',')
].join('\n');

test.describe('Bulk data review: hostile dataset through the real cross-process flow', () => {
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

  test('a poisoned row key and cell value are shown literally in the card UI and inject nothing', async ({ browser }) => {
    const context = await browser.newContext({ baseURL: REFERENCE_APP });
    const page = await context.newPage();
    await loginAs(page, DEMO_USERS.njfOperations);

    await page.goto('/caseworker/njf-contributions/new');
    await page.getByLabel('Contributions file').setInputFiles({
      name: 'contributions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(hostileCsv)
    });
    await page.getByRole('button', { name: 'Submit' }).click();
    await expect(page.getByRole('heading', { name: 'Review contributions file' })).toBeVisible({ timeout: 30_000 });

    // The card UI is client-fetched: wait for the real card, not the server-rendered "Loading…" skeleton.
    const card = page.locator('.wayfinder-bulk-review__card');
    await expect(card).toHaveCount(1, { timeout: 10_000 });
    await expect(card.locator('.wayfinder-bulk-review__card-title')).toHaveText(HOSTILE_ROW_KEY);

    const nameInput = card.getByLabel('Name');
    await expect(nameInput).toHaveValue(HOSTILE_NAME);
    await nameInput.focus();

    const result = await page.evaluate(() => {
      const input = document.querySelector('.wayfinder-bulk-review__card input[data-wayfinder-bulk-review-input="memberName"]');
      return {
        scriptRan: (window as unknown as { __pwned?: number }).__pwned === 1,
        injectedElements: document.querySelectorAll('.wayfinder-bulk-review__card img, .wayfinder-bulk-review__card [data-evil]').length,
        inputAttributes: input ? Array.from(input.attributes).map((a) => a.name).sort() : null
      };
    });

    expect(result.scriptRan, 'no script may run').toBe(false);
    expect(result.injectedElements, 'no element may be injected').toBe(0);
    expect(result.inputAttributes, 'the input carries exactly its own attributes').toEqual(
      ['class', 'data-wayfinder-bulk-review-input', 'id', 'value']
    );

    await context.close();
  });
});
