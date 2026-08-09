import { mkdirSync } from 'fs';
import { dirname, resolve } from 'path';
import { expect, type APIRequestContext, type Locator, type Page } from '@playwright/test';

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

/**
 * Writes a screenshot for the docs/skills/ library — a side effect of a real behavioural
 * assertion, never a screenshot with no assertion behind it (see docs/skills/README.md).
 * A no-op unless CAPTURE_DOC_SCREENSHOTS is set, so routine CI runs stay deterministic and
 * don't rewrite committed images on every run (cross-runner font/anti-aliasing differences
 * would otherwise produce meaningless diffs) — run `npm run docs:screenshots` locally to
 * regenerate after a real UI change, then commit the result like any other generated asset.
 * `relativePathFromRepoRoot` is relative to the repo root, e.g.
 * `docs/skills/calculations-editor/screenshots/fields-live-preview.png`.
 */
export async function captureDocScreenshot(target: Page | Locator, relativePathFromRepoRoot: string): Promise<void> {
  if (!process.env.CAPTURE_DOC_SCREENSHOTS) {
    return;
  }
  const absolutePath = resolve(process.cwd(), '..', relativePathFromRepoRoot);
  mkdirSync(dirname(absolutePath), { recursive: true });
  await target.screenshot({ path: absolutePath });
}
