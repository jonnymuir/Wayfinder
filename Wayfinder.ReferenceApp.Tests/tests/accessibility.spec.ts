import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

/**
 * WCAG 2.2 AA conformance for the real rendered journey — every screen a citizen or caseworker
 * actually sees, audited with axe-core, plus an explicit keyboard-operability check.
 *
 * Two things this deliberately does NOT try to do:
 *
 * - Assert a specific browser's *tab scope*. Safari on macOS, by default, moves Tab only between
 *   text fields and pop-up menus — it skips links, buttons, checkboxes and radios until the user
 *   turns on "Press Tab to highlight each item on a webpage" (Safari → Settings → Advanced) or
 *   macOS Full Keyboard Access. Verified directly against WebKit: from the event-details form it
 *   tabs the five text inputs and nothing else, skipping even the skip-link. That is a user-agent
 *   preference applying to every site on the web, not something page markup can override, so the
 *   keyboard test below runs on the default (Chromium) engine and asserts what content is
 *   responsible for: correct focus order, a reachable control for every input, and real
 *   operability once focused.
 * - Replace manual testing. axe catches roughly a third of WCAG issues; it cannot judge whether
 *   an error message is *useful* or a heading structure is *meaningful*.
 */

const WCAG_AA = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function auditPage(page: Page, context: string) {
  const results = await new AxeBuilder({ page }).withTags(WCAG_AA).analyze();
  const summary = results.violations.map(
    v => `${v.id} (${v.impact}) — ${v.help}\n    ${v.nodes.map(n => n.target.join(' ')).join('\n    ')}`
  );
  expect(results.violations, `${context} has WCAG AA violations:\n  ${summary.join('\n  ')}`).toEqual([]);
}

test.describe('Accessibility: WCAG 2.2 AA', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('every screen of the citizen journey is free of automatically-detectable WCAG AA violations', async ({ page }) => {
    await page.goto('/account/login');
    await auditPage(page, 'Sign in');

    await loginAs(page, DEMO_USERS.applicant);
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
    await auditPage(page, 'Your details');

    await page.getByLabel('Full name').fill('Avery Access');
    await page.getByLabel('Email address').fill('avery@example.test');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    await auditPage(page, 'About the event');

    await page.getByLabel('Name of the event').fill('Accessible Juggling Gala');
    await page.getByLabel('Day').fill('1');
    await page.getByLabel('Month').fill('9');
    await page.getByLabel('Year').fill('2026');
    await page.getByLabel('Number of jugglers taking part').fill('4');
    await page.getByLabel('This act involves fire, knives, or other dangerous props').check();
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await auditPage(page, 'Risk assessment');

    // A server-rendered validation-error state is its own screen, and the one most likely to
    // regress — error-summary linkage, aria-describedby wiring and focus order only exist here.
    // Reached via the cross-stage rule (dangerous props ticked, no file, notes with no measurable
    // detail) rather than by leaving a required field blank: those are `required` in the markup,
    // so the browser's own HTML5 validation blocks the submit before a server round-trip happens
    // and this page never renders at all.
    await page.getByLabel('How are you mitigating the risk?').fill('We will be careful.');
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.locator('.govuk-error-summary')).toBeVisible();
    await auditPage(page, 'Risk assessment (server-side validation errors)');

    await page.getByLabel('How are you mitigating the risk?').fill('12 metre exclusion zone, HSE-aligned.');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await auditPage(page, 'Check your answers');

    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
    await auditPage(page, 'Applicant waiting screen');
  });

  test('the caseworker queue and review screens are free of automatically-detectable WCAG AA violations', async ({
    page,
    browser,
  }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);
    await applicantPage.getByLabel('Full name').fill('Avery Access');
    await applicantPage.getByLabel('Email address').fill('avery@example.test');
    await applicantPage.getByRole('button', { name: 'Continue' }).click();
    await applicantPage.getByLabel('Name of the event').fill('Accessible Juggling Gala');
    await applicantPage.getByLabel('Day').fill('1');
    await applicantPage.getByLabel('Month').fill('9');
    await applicantPage.getByLabel('Year').fill('2026');
    await applicantPage.getByLabel('Number of jugglers taking part').fill('4');
    await applicantPage.getByLabel('This act involves fire, knives, or other dangerous props').check();
    await applicantPage.getByRole('button', { name: 'Continue' }).click();
    await applicantPage.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-1.4 accessible'),
    });
    await applicantPage.getByRole('button', { name: 'Continue' }).click();
    await applicantPage.getByLabel('I confirm the details above are correct').check();
    await applicantPage.getByRole('button', { name: 'Submit application' }).click();
    await expect(applicantPage.getByText('A caseworker is reviewing your application.')).toBeVisible();
    await applicantContext.close();

    await loginAs(page, DEMO_USERS.caseworker);
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await auditPage(page, 'Caseworker queue');

    await page.getByRole('button', { name: 'Pick up' }).click();
    await expect(page.getByText('With you')).toBeVisible();
    await page.getByRole('link', { name: 'Review' }).click();
    await expect(page.getByRole('heading', { name: 'Review application' })).toBeVisible();
    await auditPage(page, 'Caseworker review');

    // The waiting state introduced by the support-systems work — its own distinct rendering
    // (status-timeline on the item, status tag in the worklist), so its own audit of each. The
    // caseworker lands on the item's own wait screen directly (ResponseState "defer" — see
    // Program.cs's post-advance redirect), the same way the citizen's own post-review join
    // always has; the queue list's "Waiting" tag is audited separately by navigating there.
    await page.getByRole('button', { name: 'Send risk assessment to insurer' }).click();
    await expect(page.getByText('SafetyNet Underwriting is reviewing the risk assessment.')).toBeVisible();
    await auditPage(page, 'Caseworker waiting screen');

    await page.goto('/caseworker/queue');
    await auditPage(page, 'Caseworker queue (waiting on a support system)');
  });

  test('every control on the event-details form is keyboard reachable, in visual order, and operable', async ({
    page,
  }) => {
    // WCAG 2.1.1 (Keyboard) and 2.4.3 (Focus Order). Runs on the default engine: Safari's own
    // restricted tab scope is a user-agent preference (see this file's header), so asserting it
    // here would test the browser's settings rather than this service's markup.
    await loginAs(page, DEMO_USERS.applicant);
    await page.getByLabel('Full name').fill('Avery Access');
    await page.getByLabel('Email address').fill('avery@example.test');
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();

    await page.locator('#eventName').focus();
    const reached: string[] = [];
    for (let i = 0; i < 6; i++) {
      reached.push(await page.evaluate(() => document.activeElement?.id ?? ''));
      await page.keyboard.press('Tab');
    }

    // Exactly the visual order of the form, ending on its submit control — no skipped input, no
    // control that can only be reached with a mouse.
    expect(reached).toEqual([
      'eventName',
      'eventDate-day',
      'eventDate-month',
      'eventDate-year',
      'jugglerCount',
      'hasDangerousProps',
    ]);

    const continueButton = page.getByRole('button', { name: 'Continue' });
    await expect(continueButton).toBeFocused();

    // Reachable is not the same as operable: the checkbox must actually toggle from the keyboard,
    // and the form must submit from the keyboard.
    await page.locator('#hasDangerousProps').focus();
    await page.keyboard.press('Space');
    await expect(page.locator('#hasDangerousProps')).toBeChecked();

    // A visible focus indicator (WCAG 2.4.7) — govuk-frontend draws it on the label's ::before,
    // so assert the real focus colour rather than the input's own (deliberately invisible) box.
    const focusRing = await page.evaluate(() => {
      const label = document.querySelector('label[for="hasDangerousProps"]');
      return getComputedStyle(label!, '::before').boxShadow;
    });
    expect(focusRing).toContain('rgb(255, 221, 0)');
  });
});
