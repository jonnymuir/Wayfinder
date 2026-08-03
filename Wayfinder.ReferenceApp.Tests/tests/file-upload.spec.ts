import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// The "does your act involve fire, knives, or other dangerous props?" branch on the juggling
// licence blueprint — a conditionally-required file-upload field (risk assessment / public
// liability insurance), gated by a calc-driven showWhen on the *next* stage (declaration), since
// this reference app has no client-side live-form runtime to reveal a same-page field the moment
// a checkbox changes — the reveal only becomes real once the answer is actually persisted.
async function completeYourDetailsAndEventDetails(page: import('@playwright/test').Page, options: { dangerousProps: boolean }) {
  await loginAs(page, DEMO_USERS.applicant);
  await page.getByLabel('Full name').fill('Alex Applicant');
  await page.getByLabel('Email address').fill('alex@example.test');
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
  await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
  await page.getByLabel('Day').fill('1');
  await page.getByLabel('Month').fill('9');
  await page.getByLabel('Year').fill('2026');
  await page.getByLabel('Number of jugglers taking part').fill('12');
  if (options.dangerousProps) {
    await page.getByLabel('This act involves fire, knives, or other dangerous props').check();
  }
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
}

test.describe('File upload: risk assessment for dangerous props', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('answering no never shows or requires the file upload', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: false });

    // Still present in the DOM (behind a `hidden` component wrapper, not removed) — a live-form
    // JS runtime could reveal it without a full reload; this reference app has none, so it only
    // ever becomes real (and required) via the persisted answer on the next stage's render.
    await expect(page.getByLabel('Upload your risk assessment or public liability insurance certificate')).toBeHidden();

    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    await expect(page.getByRole('heading', { name: 'Application under review' })).toBeVisible();
  });

  test('answering yes reveals the upload and requires it before submitting', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });

    const upload = page.getByLabel('Upload your risk assessment or public liability insurance certificate');
    await expect(upload).toBeVisible();

    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    // Browser-native `required` on a real, visible file input blocks the submit — still on
    // the same stage, not a server round trip (mirrors the existing plain-required-field spec).
    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
  });

  test('a valid file completes the journey', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });

    await page.getByLabel('Upload your risk assessment or public liability insurance certificate').setInputFiles({
      name: 'risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-1.4 test risk assessment content'),
    });
    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    await expect(page.getByRole('heading', { name: 'Application under review' })).toBeVisible();
  });

  test('an oversized file is rejected server-side, with a field-scoped error', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });

    // 6MB — over the blueprint's declared 5MB maxSizeBytes. A real <input type="file"> has no
    // client-side size constraint of its own, so this only ever gets caught server-side.
    await page.getByLabel('Upload your risk assessment or public liability insurance certificate').setInputFiles({
      name: 'oversized.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.alloc(6 * 1024 * 1024, '0'),
    });
    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await expect(page.locator('.govuk-error-message', { hasText: 'must be smaller than 5MB' })).toBeVisible();
  });

  test('a disallowed file type is rejected server-side, with a field-scoped error', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });

    await page.getByLabel('Upload your risk assessment or public liability insurance certificate').setInputFiles({
      name: 'malware.exe',
      mimeType: 'application/octet-stream',
      buffer: Buffer.from('not a real risk assessment'),
    });
    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await expect(page.locator('.govuk-error-message', { hasText: 'must be one of' })).toBeVisible();
  });
});
