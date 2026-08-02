import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// The applicant's frontstage journey through "Apply for a licence to hold a juggling event" —
// GOV.UK Service Manual's own teaching exemplar (see JugglingLicenceBlueprint.cs). This spec
// walks every citizen-queue stage up to handoff, proving the engine's stage/route/queue model
// end to end through the reference app's own hand-rolled HTML forms, not just via API calls.
test.describe('Citizen journey: apply for a juggling licence', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('an applicant can complete every citizen-queue stage up to caseworker handoff', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);

    await test.step('Your details', async () => {
      await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
      await page.getByLabel('Full name').fill('Alex Applicant');
      await page.getByLabel('Email address').fill('alex@example.test');
      await page.getByRole('button', { name: 'Continue' }).click();
    });

    await test.step('About the event', async () => {
      await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
      await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
      // The real GOV.UK date-input component: three separate day/month/year boxes, not a
      // native date picker — see ComponentHtmlRenderer.RenderDateField.
      await page.getByLabel('Day').fill('1');
      await page.getByLabel('Month').fill('9');
      await page.getByLabel('Year').fill('2026');
      await page.getByLabel('Number of jugglers taking part').fill('12');
      await page.getByRole('button', { name: 'Continue' }).click();
    });

    await test.step('Check your answers and declare', async () => {
      await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
      const summary = page.locator('.govuk-summary-list');
      await expect(summary.getByText('Alex Applicant', { exact: true })).toBeVisible();
      await expect(summary.getByText('Big Top Juggling Gala', { exact: true })).toBeVisible();
      await expect(summary.getByText('1 September 2026', { exact: true })).toBeVisible();
      await page.getByLabel('I confirm the details above are correct').check();
      await page.getByRole('button', { name: 'Submit application' }).click();
    });

    await test.step('Handed off to the caseworker queue', async () => {
      await expect(page.getByRole('heading', { name: 'Application under review' })).toBeVisible();
      // No actions available here — approve/reject belong to the caseworker's backstage lane.
      await expect(page.getByRole('button', { name: 'Approve' })).toHaveCount(0);
    });

    await test.step('Reloading the journey resumes the same in-progress instance', async () => {
      await page.reload();
      await expect(page.getByRole('heading', { name: 'Application under review' })).toBeVisible();
    });
  });

  test('required fields block progress before a value is entered', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();

    // The browser's own HTML5 `required` validation stops the submit — client-side, mirroring
    // what a caseworker/citizen actually experiences, and cheap to prove without a server round trip.
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
  });
});
