import { test, expect, type Page } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// The full two-actor handoff this reference app exists to demonstrate: an applicant's
// frontstage submission lands in the caseworker's backstage queue (NN/g's service-blueprint
// lanes — https://www.nngroup.com/articles/service-blueprints-definition/), and the
// caseworker's decision is visible back to the applicant immediately, in-process, with no
// persistence beyond this run.
test.describe('Caseworker queue: review and decide', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  async function submitApplication(applicantPage: Page): Promise<void> {
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
    // The applicant waits at their own Join gateway cursor — see citizen-journey.spec.ts for
    // why this is a genuine "please wait" status, not ACCESS_DENIED or the caseworker's own
    // stage content.
    await expect(applicantPage.getByText('A caseworker is reviewing your application.')).toBeVisible();
  }

  test('a caseworker can approve an application and the applicant sees the outcome', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);

    await test.step('Applicant submits an application', async () => submitApplication(applicantPage));

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);

    await test.step('Caseworker sees it waiting in their queue and picks it up', async () => {
      // juggling-licence's own caseworker queue is team-tray (see docs/guides/team-assignment.md)
      // — a not-picked-up row shows "View" (not "Review") and must be explicitly picked up before
      // it's actionable at all.
      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      await caseworkerPage.getByRole('button', { name: 'Pick up' }).click();
      await expect(caseworkerPage.getByText('With you')).toBeVisible();
      await caseworkerPage.getByRole('link', { name: 'Review' }).click();
    });

    await test.step("Caseworker sees the applicant's submitted details and approves", async () => {
      // Reviewing and deciding are separate stages: this application carries no risk assessment,
      // so "Continue to decision" is allowed straight away (with one attached, the blueprint's
      // action-scoped validation rule forces it to the insurer first).
      await expect(caseworkerPage.getByRole('heading', { name: 'Review application' })).toBeVisible();
      await expect(caseworkerPage.getByText('Big Top Juggling Gala')).toBeVisible();
      await caseworkerPage.getByRole('button', { name: 'Continue to decision' }).click();

      await expect(caseworkerPage.getByRole('heading', { name: 'Record your decision' })).toBeVisible();
      await caseworkerPage.getByRole('button', { name: 'Approve' }).click();
      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      // juggling-licence's own "approve" route hands off entirely into the CITIZEN queue's own
      // post-review Join (see the blueprint's own gateway graph) — the caseworker's cursor has no
      // accessible work item left in their own queue at all once approved, "Done" filter or not.
      // See njf-contributions-concurrency-live.spec.ts for the worklist's status filter/search UI
      // proven against a blueprint whose caseworker queue genuinely does reach "Done".
      await expect(caseworkerPage.getByText('No applications match the current filters')).toBeVisible();
    });

    await test.step('Applicant sees the granted licence', async () => {
      await applicantPage.reload();
      await expect(applicantPage.getByRole('heading', { name: 'Licence granted' })).toBeVisible();
    });

    await applicantContext.close();
    await caseworkerContext.close();
  });

  test('a caseworker can reject an application and the applicant sees the outcome', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);
    await submitApplication(applicantPage);

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);
    await caseworkerPage.getByRole('button', { name: 'Pick up' }).click();
    await expect(caseworkerPage.getByText('With you')).toBeVisible();
    await caseworkerPage.getByRole('link', { name: 'Review' }).click();
    await caseworkerPage.getByRole('button', { name: 'Continue to decision' }).click();
    await expect(caseworkerPage.getByRole('heading', { name: 'Record your decision' })).toBeVisible();
    await caseworkerPage.getByRole('button', { name: 'Reject' }).click();

    await applicantPage.reload();
    await expect(applicantPage.getByRole('heading', { name: 'Application not approved' })).toBeVisible();

    await applicantContext.close();
    await caseworkerContext.close();
  });
});
