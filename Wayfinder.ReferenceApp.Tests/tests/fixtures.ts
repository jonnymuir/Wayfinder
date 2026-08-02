import { expect, type APIRequestContext, type Page } from '@playwright/test';

/**
 * The reference app's entire "identity provider" — see
 * Wayfinder.ReferenceApp/Services/DemoUsers.cs. Dev-only credentials for a transient host
 * meant to be booted and reset constantly by this suite.
 */
export const DEMO_USERS = {
  applicant: { email: 'applicant@example.test', password: 'wayfinder-demo' },
  caseworker: { email: 'caseworker@example.test', password: 'wayfinder-demo' }
} as const;

export type DemoUser = (typeof DEMO_USERS)[keyof typeof DEMO_USERS];

/** Wipes every in-memory service request instance and authoring override — see `/api/test/reset`. */
export async function resetApp(request: APIRequestContext): Promise<void> {
  const response = await request.delete('/api/test/reset');
  expect(response.ok(), 'DELETE /api/test/reset should succeed in Development').toBeTruthy();
}

export async function loginAs(page: Page, user: DemoUser): Promise<void> {
  await page.goto('/account/login');
  await page.getByLabel('Email address').fill(user.email);
  // Not getByLabel('Password') — the real GOV.UK password-input component's "Show" toggle
  // button has an aria-label of "Show password", which also substring-matches "Password".
  await page.locator('#password').fill(user.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}
