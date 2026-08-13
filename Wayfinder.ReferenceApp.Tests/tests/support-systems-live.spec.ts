import { test, expect, type Page } from '@playwright/test';
import { LiveAppHost } from './support/live-app-host';
import { DEMO_USERS, loginAs } from './fixtures';

// Real cross-process integration: proves the "send to insurer" flow actually works between two
// genuinely separate, Aspire-orchestrated apps — not just that each one's own code is internally
// consistent. Needs its own AppHost lifecycle (see live-app-host.ts's own doc comment for why
// this can't run under the default playwright.config.ts's single-process webServer) — run with
// `npm run test:playwright:live`, not the default `npm run test:playwright`.
const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';

const appHost = new LiveAppHost();

async function completeApplicationUpToRiskAssessment(page: Page, fileContent: string) {
  await loginAs(page, DEMO_USERS.applicant);
  await page.getByLabel('Full name').fill('Jamie Applicant');
  await page.getByLabel('Email address').fill('jamie@example.test');
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
  await page.getByLabel('Name of the event').fill('Live-Stack Fire Juggling Show');
  await page.getByLabel('Day').fill('15');
  await page.getByLabel('Month').fill('9');
  await page.getByLabel('Year').fill('2026');
  await page.getByLabel('Number of jugglers taking part').fill('3');
  await page.getByLabel('This act involves fire, knives, or other dangerous props').check();
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
  await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
    name: 'risk-assessment.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from(fileContent)
  });
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
  await page.getByLabel('I confirm the details above are correct').check();
  await page.getByRole('button', { name: 'Submit application' }).click();
  await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
}

test.describe('Support systems: real cross-process round trip', () => {
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

  test('a real webhook from SafetyNet Underwriting\'s own app resolves the caseworker\'s wait, all the way through to a granted licence', async ({
    browser
  }) => {
    const fileContent = '%PDF-1.4 live-stack risk assessment content';

    const applicantContext = await browser.newContext({ baseURL: REFERENCE_APP });
    const applicantPage = await applicantContext.newPage();
    await completeApplicationUpToRiskAssessment(applicantPage, fileContent);

    const caseworkerContext = await browser.newContext({ baseURL: REFERENCE_APP });
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);
    await caseworkerPage.getByRole('link', { name: 'Review' }).click();
    await caseworkerPage.getByText('Live-Stack Fire Juggling Show').click();

    await expect(caseworkerPage.getByRole('heading', { name: 'Application under review' })).toBeVisible();
    const caseworkerItemUrl = caseworkerPage.url();
    await caseworkerPage.getByRole('button', { name: 'Send risk assessment to insurer' }).click();

    // The advance route always redirects to the queue list (Program.cs), and this instance
    // correctly drops off that list while parked at the join with no caseworker-actionable
    // routes — go back to its own URL directly, the same way a caseworker would via their
    // browser history/a bookmark, or this reference app's own "my in-progress items" concept
    // would (there isn't one yet — see docs/guides/reference-app.md).
    await caseworkerPage.goto(caseworkerItemUrl);

    // The caseworker's own cursor is now parked at the insurer-check-complete Join gateway,
    // waiting exactly like the citizen's post-review join already did — same wait/poll UI.
    await expect(caseworkerPage.getByText('SafetyNet Underwriting is reviewing the risk assessment.')).toBeVisible();

    // A genuinely separate app, browser-driven in its own context — not an API shortcut. The
    // real uploaded file travelled server-to-server: this is what proves it (no live-app-host
    // seam, no shared in-memory store — SafetyNetUnderwriting only knows what
    // SafetyNetUnderwritingClient actually sent it over HTTP).
    const safetyNetContext = await browser.newContext({ baseURL: SAFETYNET });
    const safetyNetPage = await safetyNetContext.newPage();
    await safetyNetPage.goto('/queue');
    await expect(safetyNetPage.getByText('Jamie Applicant')).toBeVisible();
    await expect(safetyNetPage.getByText('Live-Stack Fire Juggling Show')).toBeVisible();
    await expect(safetyNetPage.getByText('risk-assessment.pdf')).toBeVisible();

    const pendingRow = safetyNetPage.locator('tr', { hasText: 'Jamie Applicant' });
    await pendingRow.getByLabel('Decision notes').fill('Adequate mitigation for a live-stack test.');
    await pendingRow.getByRole('button', { name: 'Approve' }).click();

    // The webhook SafetyNetUnderwriting just fired (POST /wayfinder/support-systems/callbacks/
    // {invocationId}) should have already resolved the caseworker's wait — reload rather than
    // poll, proving this is push-driven, not the poll-check hook papering over a missed webhook.
    await caseworkerPage.reload();
    await expect(caseworkerPage.getByRole('heading', { name: 'Application under review' })).toBeVisible();
    const summary = caseworkerPage.locator('.govuk-summary-list');
    await expect(summary.getByText('approved', { exact: true })).toBeVisible();
    await expect(summary.getByText('Adequate mitigation for a live-stack test.')).toBeVisible();

    await caseworkerPage.getByRole('button', { name: 'Approve' }).click();

    await applicantPage.reload();
    await expect(applicantPage.getByRole('heading', { name: 'Licence granted' })).toBeVisible();

    await applicantContext.close();
    await caseworkerContext.close();
    await safetyNetContext.close();
  });
});
