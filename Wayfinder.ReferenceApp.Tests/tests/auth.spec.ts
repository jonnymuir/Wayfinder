import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// This reference app's entire auth boundary: a hand-rolled in-memory cookie login (see
// Wayfinder.ReferenceApp/Services/DemoUsers.cs for why it's not OIDC/Keycloak here), gating
// the citizen (frontstage) and caseworker (backstage) request-processing screens.
test.describe('Authentication', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('an unauthenticated visitor is redirected to sign in from the applicant journey', async ({ page }) => {
    await page.goto('/apply');
    await expect(page).toHaveURL(/\/account\/login/);
  });

  test('an unauthenticated visitor is redirected to sign in from the caseworker queue', async ({ page }) => {
    await page.goto('/caseworker/queue');
    await expect(page).toHaveURL(/\/account\/login/);
  });

  test('an unrecognised email/password combination is rejected', async ({ page }) => {
    await page.goto('/account/login');
    await page.getByLabel('Email address').fill('nobody@example.test');
    await page.locator('#password').fill('wrong-password');
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page.getByText('There is a problem')).toBeVisible();
    await expect(page).toHaveURL(/\/account\/login/);
  });

  test('the applicant demo account signs in and lands on their own journey', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await expect(page).toHaveURL(/\/apply/);
    await expect(page.getByText('Signed in as Alex Applicant')).toBeVisible();
  });

  test('the caseworker demo account signs in and lands on the backstage queue', async ({ page }) => {
    await loginAs(page, DEMO_USERS.caseworker);
    await expect(page).toHaveURL(/\/caseworker\/queue/);
    await expect(page.getByText('Signed in as Casey Caseworker')).toBeVisible();
  });

  test('the applicant lane cannot reach the caseworker queue, and vice versa', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await page.goto('/caseworker/queue');
    await expect(page).not.toHaveURL(/\/caseworker\/queue/);

    await page.getByRole('button', { name: 'Sign out' }).click();

    await loginAs(page, DEMO_USERS.caseworker);
    await page.goto('/apply');
    await expect(page).not.toHaveURL(/^http:\/\/127\.0\.0\.1:\d+\/apply$/);
  });

  test('signing out ends the session', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/account\/login/);

    await page.goto('/apply');
    await expect(page).toHaveURL(/\/account\/login/);
  });
});
