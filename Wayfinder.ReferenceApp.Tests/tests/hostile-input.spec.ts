import { test, expect, type Page } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Hostile data through the real app. The security scanners in CI never send hostile input (CodeQL reads
// code, ZAP's baseline is passive), so this is the only check that an applicant's free text cannot break
// out of the page that renders it back, on any screen it appears on: the form re-rendered after a
// validation error (a reflected value inside value="…"), "check your answers", and the caseworker's
// review of the submitted application.
//
// The assertions are about the DOM, not about a script running: the reference app's strict CSP would stop
// an injected inline handler executing, but attribute and element injection still happens under it, and a
// host without that CSP would run the script. So: the text must appear literally, and nothing may have
// been injected.
const HOSTILE_ELEMENT = '<img src=x onerror="window.__pwned=1">';
const HOSTILE_ATTRIBUTE = 'x" autofocus onfocus="window.__pwned=1" data-evil="1';
const HOSTILE_INVALID_EMAIL = '"><img src=x onerror="window.__pwned=1">';

async function expectNothingInjected(page: Page, where: string): Promise<void> {
  const result = await page.evaluate(() => ({
    scriptRan: (window as unknown as { __pwned?: number }).__pwned === 1,
    injectedElements: document.querySelectorAll('img[src="x"], [data-evil]').length,
    injectedHandlers: Array.from(document.querySelectorAll('*')).filter(
      (element) => element.hasAttribute('onfocus') || element.hasAttribute('onerror')
    ).length
  }));
  expect(result, `nothing hostile may be injected into: ${where}`).toEqual({
    scriptRan: false,
    injectedElements: 0,
    injectedHandlers: 0
  });
}

test.describe('Hostile input through the juggling-licence journey', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('an applicant\'s free text is rendered inert on every screen it appears on, including the caseworker\'s', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicant = await applicantContext.newPage();
    await loginAs(applicant, DEMO_USERS.applicant);

    await test.step('a rejected submission re-renders the hostile values inside the form without breaking out', async () => {
      await expect(applicant.getByRole('heading', { name: 'Your details' })).toBeVisible();
      await applicant.getByLabel('Full name').fill(HOSTILE_ELEMENT);
      await applicant.getByLabel('Email address').fill(HOSTILE_INVALID_EMAIL);
      // type="email" would stop the browser posting this at all. A real attacker does not use the
      // browser's validation, so switch it off to make the server do the rejecting and the re-render.
      await applicant.locator('main form').evaluate((form) => { (form as HTMLFormElement).noValidate = true; });
      await applicant.getByRole('button', { name: 'Continue' }).click();

      await expect(applicant.getByRole('heading', { name: 'There is a problem' })).toBeVisible();
      await expect(applicant.getByLabel('Email address')).toHaveValue(HOSTILE_INVALID_EMAIL);
      await expect(applicant.getByLabel('Full name')).toHaveValue(HOSTILE_ELEMENT);
      await expectNothingInjected(applicant, 'the re-rendered "Your details" form');
    });

    await test.step('the details are accepted once the email is valid', async () => {
      await applicant.getByLabel('Email address').fill('alex@example.test');
      await applicant.getByRole('button', { name: 'Continue' }).click();
      await expect(applicant.getByRole('heading', { name: 'About the event' })).toBeVisible();
      await applicant.getByLabel('Name of the event').fill(HOSTILE_ATTRIBUTE);
      await applicant.getByLabel('Day').fill('1');
      await applicant.getByLabel('Month').fill('9');
      await applicant.getByLabel('Year').fill('2026');
      await applicant.getByLabel('Number of jugglers taking part').fill('12');
      await applicant.getByRole('button', { name: 'Continue' }).click();
      await applicant.getByRole('button', { name: 'Continue' }).click(); // Risk assessment, optional
    });

    await test.step('"check your answers" shows the text literally and injects nothing', async () => {
      await expect(applicant.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
      const summary = applicant.locator('.govuk-summary-list');
      await expect(summary.getByText(HOSTILE_ELEMENT, { exact: true })).toBeVisible();
      await expect(summary.getByText(HOSTILE_ATTRIBUTE, { exact: true })).toBeVisible();
      await expectNothingInjected(applicant, '"Check your answers"');
      await applicant.getByLabel('I confirm the details above are correct').check();
      await applicant.getByRole('button', { name: 'Submit application' }).click();
      await expect(applicant.getByRole('heading', { name: 'Application under review' })).toBeVisible();
    });

    await test.step('the caseworker\'s review of the application shows the text literally and injects nothing', async () => {
      const caseworkerContext = await browser.newContext();
      const caseworker = await caseworkerContext.newPage();
      await loginAs(caseworker, DEMO_USERS.caseworker);
      await expectNothingInjected(caseworker, 'the caseworker queue');
      await caseworker.getByRole('button', { name: 'Pick up' }).click();
      await caseworker.getByRole('link', { name: 'Review' }).click();

      await expect(caseworker.getByText(HOSTILE_ATTRIBUTE, { exact: true })).toBeVisible();
      await expectNothingInjected(caseworker, 'the caseworker\'s review page');
      await caseworkerContext.close();
    });

    await applicantContext.close();
  });
});
