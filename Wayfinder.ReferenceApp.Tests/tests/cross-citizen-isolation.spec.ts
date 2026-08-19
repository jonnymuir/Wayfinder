import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Real coverage that two different citizens can't reach each other's own instance — the mechanism
// (ProcessManagerEngine.CanAccessInstance plus the ambient FindLatestInstance scoping) already
// worked before this session's team-assignment work, but had zero test coverage, since there was
// only ever one applicant demo user to check it with. See docs/guides/team-assignment.md.
test.describe('Cross-citizen isolation', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test("Alex and Jamie each get their own application — neither can reach the other's", async ({ browser }) => {
    const alexContext = await browser.newContext();
    const alexPage = await alexContext.newPage();
    await loginAs(alexPage, DEMO_USERS.applicant);

    const jamieContext = await browser.newContext();
    const jamiePage = await jamieContext.newPage();
    await loginAs(jamiePage, DEMO_USERS.secondApplicant);

    await test.step("Alex starts an application and fills in their own name", async () => {
      await expect(alexPage.getByRole('heading', { name: 'Your details' })).toBeVisible();
      await alexPage.getByLabel('Full name').fill('Alex Applicant');
      await alexPage.getByLabel('Email address').fill('alex@example.test');
      await alexPage.getByRole('button', { name: 'Continue' }).click();
      await expect(alexPage.getByRole('heading', { name: 'About the event' })).toBeVisible();
    });

    await test.step("Jamie starts their own, completely separate application — no trace of Alex's own in-progress answers", async () => {
      await expect(jamiePage.getByRole('heading', { name: 'Your details' })).toBeVisible();
      await expect(jamiePage.getByLabel('Full name')).toHaveValue('');
      await jamiePage.getByLabel('Full name').fill('Jamie Applicant');
      await jamiePage.getByLabel('Email address').fill('jamie@example.test');
      await jamiePage.getByRole('button', { name: 'Continue' }).click();
      await expect(jamiePage.getByRole('heading', { name: 'About the event' })).toBeVisible();
    });

    await test.step("Alex reloading their own journey still sees their own progress, not Jamie's", async () => {
      await alexPage.reload();
      await expect(alexPage.getByRole('heading', { name: 'About the event' })).toBeVisible();
    });

    await alexContext.close();
    await jamieContext.close();
  });
});
