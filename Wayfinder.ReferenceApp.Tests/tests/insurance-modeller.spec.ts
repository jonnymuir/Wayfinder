import { test, expect, type Page } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// A second citizen/caseworker demo alongside citizen-journey/caseworker-queue.spec.ts — showcases
// slider/stat-group/chart-driven interactive modelling (live, client-side recalculation via
// wayfinder-live-form.js — see that file's own comments) feeding the same applicant/caseworker
// Split-into-[stage+Join] handoff pattern the juggling-licence demo uses, proving the caseworker
// queue and per-instance review routes are genuinely multi-blueprint (see Program.cs's
// /caseworker/queue/{blueprintKey}/{instanceId} routes).
test.describe('Insurance premium modeller: model, request, review', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  async function modelPremium(applicantPage: Page): Promise<void> {
    await applicantPage.goto('/premium');
    await expect(applicantPage.getByRole('heading', { name: 'Model your performance insurance premium' })).toBeVisible();

    await applicantPage.getByLabel('Competitive').check();
    await applicantPage.getByLabel('Years you\'ve held a licence').fill('6');
    await applicantPage.getByLabel('Contact & balance').check();
    await applicantPage.locator('#performancesPerYear').fill('30');
    await applicantPage.locator('#averageAudienceSize').fill('500');

    await expect(applicantPage.getByText('TOTAL ANNUAL PREMIUM', { exact: false })).toBeVisible();
  }

  test('an applicant can model a premium, send it for review, and see the caseworker\'s decision', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);

    await test.step('Applicant models a premium and recalculates', async () => modelPremium(applicantPage));

    await test.step('Applicant sends it to a caseworker and waits', async () => {
      await applicantPage.getByRole('button', { name: 'Send to a caseworker' }).click();
      await expect(applicantPage.getByText('A caseworker is reviewing your modelled premium.')).toBeVisible();
    });

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);

    await test.step('Caseworker sees it in the shared queue alongside the licence demo', async () => {
      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
      await expect(caseworkerPage.getByText('Model your performance insurance premium')).toBeVisible();
      await caseworkerPage.getByRole('link', { name: 'Review' }).click();
    });

    await test.step('Caseworker sees the modelled scenario and confirms the premium', async () => {
      await expect(caseworkerPage.getByRole('heading', { name: 'Review premium request' })).toBeVisible();
      await expect(caseworkerPage.getByText('Competitive')).toBeVisible();
      await expect(caseworkerPage.getByText('Contact & balance')).toBeVisible();
      await caseworkerPage.getByRole('button', { name: 'Confirm premium' }).click();
      await expect(caseworkerPage.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    });

    await test.step('Applicant sees the confirmed premium', async () => {
      await applicantPage.reload();
      await expect(applicantPage.getByRole('heading', { name: 'Your premium has been confirmed' })).toBeVisible();
    });

    await test.step('A reload keeps showing the same confirmation — an ordinary visit must not silently reset it', async () => {
      // requestPolicy "single" is deliberately sticky on ambient GetCurrent — a returning
      // applicant sees "confirmed", not a blank form (see JourneyExtensions.MapJourney's own
      // remarks). Only the distinct "Model another premium" link (below) starts fresh.
      await applicantPage.reload();
      await expect(applicantPage.getByRole('heading', { name: 'Your premium has been confirmed' })).toBeVisible();
    });

    await test.step('"Model another premium" is the explicit, distinct way to start fresh', async () => {
      await applicantPage.getByRole('link', { name: 'Model another premium' }).click();
      await expect(applicantPage.getByRole('heading', { name: 'Model your performance insurance premium' })).toBeVisible();
      // A genuinely fresh instance, not the same confirmed one re-rendered — no stale total left
      // over from the first modelling round.
      await expect(applicantPage.locator('.wayfinder-stat-card--emphasis .wayfinder-stat-card__value')).toHaveText('£64');
    });

    await applicantContext.close();
    await caseworkerContext.close();
  });

  test('the modelled premium updates live as inputs change, with no button click', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await page.goto('/premium');

    const total = page.locator('.wayfinder-stat-card--emphasis .wayfinder-stat-card__value');
    await expect(total).toHaveText('£64');

    // A radio change should recalculate instantly, client-side, with no server round trip at
    // all — see wayfinder-live-form.js.
    await page.getByLabel('Professional').check();
    await expect(total).toHaveText('£204');

    // A keyboard-driven slider change should recalculate live too, and must not lose focus
    // partway through — no DOM-swap here at all now, so focus was never actually at risk, but
    // this still proves the same "change" event drives it.
    await page.locator('#performancesPerYear').focus();
    await page.keyboard.press('ArrowRight');
    await expect(total).not.toHaveText('£204');
    await expect(page.locator('#performancesPerYear')).toBeFocused();
  });

  test('recalculation is genuinely local — zero network requests, no server-side calc runs at all', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await page.goto('/premium');

    // The live model (calculation set, input types/defaults, service-sourced values — see
    // ProcessManagerEngine.BuildLiveModel) is embedded once at render time; wayfinder-live-form.js
    // re-evaluates it via wayfinder-calculations.js on every change, never asking the server.
    const liveModel = await page.locator('script[data-wayfinder-live-model]').textContent();
    expect(liveModel).toBeTruthy();
    expect(JSON.parse(liveModel!).calculations.fields).toHaveProperty('basePremium');

    // There's no "Recalculate" action left on this blueprint's model stage at all — the route
    // and its pass-through gateway were removed once client-side recalculation made them
    // unnecessary (see juggling-insurance-modeller.json).
    await expect(page.getByRole('button', { name: 'Recalculate' })).toHaveCount(0);

    let postRequests = 0;
    page.on('request', (request) => {
      if (request.method() === 'POST') postRequests++;
    });

    await page.getByLabel('Professional').check();
    await expect(page.locator('[data-wayfinder-stat-field="basePremium"] .wayfinder-stat-card__value')).toHaveText('£180');

    expect(postRequests).toBe(0);
  });

  test('a caseworker can refer a request to a broker instead', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await loginAs(applicantPage, DEMO_USERS.applicant);
    await modelPremium(applicantPage);
    await applicantPage.getByRole('button', { name: 'Send to a caseworker' }).click();

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);
    await caseworkerPage.getByRole('link', { name: 'Review' }).click();
    await caseworkerPage.getByRole('button', { name: 'Refer to a broker' }).click();

    await applicantPage.reload();
    await expect(applicantPage.getByRole('heading', { name: 'Referred to a broker' })).toBeVisible();

    await applicantContext.close();
    await caseworkerContext.close();
  });
});
