import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Real coverage for the team view (see docs/guides/team-assignment.md) — juggling-licence.json's
// own caseworker queue is team-tray, so Casey and Jordan (both on "Juggling Licence Team") must
// genuinely contend for the same row, unlike Casey/Priya's own capability-partitioned split, which
// never exercises this. Zero coverage of this existed before this spec.
test.describe('Team worklist: shared tray, personal vs team view', () => {
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

  test('Jordan picks up a row from the shared tray; the team view still shows it to Casey, the personal one does not', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);
    await submitApplication(applicantPage);
    await applicantContext.close();

    const caseyContext = await browser.newContext();
    const caseyPage = await caseyContext.newPage();
    await loginAs(caseyPage, DEMO_USERS.caseworker);

    const jordanContext = await browser.newContext();
    const jordanPage = await jordanContext.newPage();
    await loginAs(jordanPage, DEMO_USERS.secondCaseworker);

    await test.step('Both Casey and Jordan see the unpicked row in their own personal worklist', async () => {
      await expect(caseyPage.getByRole('button', { name: 'Pick up' })).toBeVisible();
      await expect(jordanPage.getByRole('button', { name: 'Pick up' })).toBeVisible();
    });

    await test.step('Jordan picks it up', async () => {
      await jordanPage.getByRole('button', { name: 'Pick up' }).click();
      await expect(jordanPage.getByText('With you')).toBeVisible();
    });

    await test.step("Casey's own personal worklist no longer shows it at all", async () => {
      await caseyPage.reload();
      await expect(caseyPage.getByText('No applications match the current filters')).toBeVisible();
    });

    await test.step("The team view still shows it to Casey — assigned to Jordan, not hidden", async () => {
      await caseyPage.getByRole('link', { name: 'Juggling Licence Team' }).click();
      await expect(caseyPage.getByRole('heading', { name: 'Team queue' })).toBeVisible();
      // The worklist row itself shows the service/stage, not application-specific field values
      // (those only appear on the item's own review page) — "Review application" is the stage
      // display name for "under-review", the caseworker's own review stage.
      await expect(caseyPage.getByText('Apply for a licence to hold a juggling event')).toBeVisible();
      await expect(caseyPage.getByText('Review application')).toBeVisible();
    });

    await test.step('Jordan puts it back in the tray', async () => {
      await jordanPage.getByRole('button', { name: 'Put back' }).click();
      await expect(jordanPage.getByRole('button', { name: 'Pick up' })).toBeVisible();
    });

    await test.step('Visible again to both in their own personal worklist', async () => {
      await caseyPage.getByRole('link', { name: 'My work' }).click();
      await expect(caseyPage.getByRole('button', { name: 'Pick up' })).toBeVisible();
      await jordanPage.reload();
      await expect(jordanPage.getByRole('button', { name: 'Pick up' })).toBeVisible();
    });

    await caseyContext.close();
    await jordanContext.close();
  });
});
