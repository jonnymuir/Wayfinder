import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Real coverage for Claim/Release on the caseworker worklist (see
// docs/guides/work-allocation.md) — written before the Wayfinder.Engine.Worklist package
// extraction as a genuine pre-port/post-port regression proof, not added after the fact to match
// whatever the port happens to produce. Zero coverage of this existed before this spec.
test.describe('Caseworker worklist: claim and release', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  async function submitApplication(applicantPage: import('@playwright/test').Page): Promise<void> {
    await applicantPage.getByLabel('Full name').fill('Alex Applicant');
    await applicantPage.getByLabel('Email address').fill('alex@example.test');
    await applicantPage.getByRole('button', { name: 'Continue' }).click();

    await applicantPage.getByLabel('Name of the event').fill('Big Top Juggling Gala');
    await applicantPage.getByLabel('Day').fill('1');
    await applicantPage.getByLabel('Month').fill('9');
    await applicantPage.getByLabel('Year').fill('2026');
    await applicantPage.getByLabel('Number of jugglers taking part').fill('12');
    await applicantPage.getByRole('button', { name: 'Continue' }).click();

    await applicantPage.getByRole('button', { name: 'Continue' }).click(); // Risk assessment: optional, skipped.

    await applicantPage.getByLabel('I confirm the details above are correct').check();
    await applicantPage.getByRole('button', { name: 'Submit application' }).click();
    await expect(applicantPage.getByText('A caseworker is reviewing your application.')).toBeVisible();
  }

  test('claiming a worklist row hides it from the release-only caseworker view, releasing returns it to the pool', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);
    await submitApplication(applicantPage);
    await applicantContext.close();

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);

    await test.step('An unclaimed row shows a Claim button', async () => {
      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      await expect(caseworkerPage.getByRole('button', { name: 'Claim' })).toBeVisible();
      await expect(caseworkerPage.getByText('Claimed by you')).not.toBeVisible();
    });

    await test.step('Claiming shows "Claimed by you" and a Release button', async () => {
      await caseworkerPage.getByRole('button', { name: 'Claim' }).click();

      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      await expect(caseworkerPage.getByText('Claimed by you')).toBeVisible();
      await expect(caseworkerPage.getByRole('button', { name: 'Release' })).toBeVisible();
      await expect(caseworkerPage.getByRole('button', { name: 'Claim' })).not.toBeVisible();
    });

    await test.step('The item is still fully reviewable while claimed', async () => {
      await caseworkerPage.getByRole('link', { name: 'Review' }).click();
      await expect(caseworkerPage.getByRole('heading', { name: 'Review application' })).toBeVisible();
      await caseworkerPage.goBack();
    });

    await test.step('Releasing returns the row to the unclaimed pool', async () => {
      await caseworkerPage.getByRole('button', { name: 'Release' }).click();

      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      await expect(caseworkerPage.getByRole('button', { name: 'Claim' })).toBeVisible();
      await expect(caseworkerPage.getByText('Claimed by you')).not.toBeVisible();
    });

    await caseworkerContext.close();
  });
});
