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

/**
 * Accept a CodeMirror autocomplete option with Enter, without racing CodeMirror's own debounce.
 *
 * The obvious sequence — `pressSequentially('cla')`, assert the tooltip is visible and contains
 * "clamp", press Enter — is genuinely flaky (reproduced at roughly 1 run in 3). Every one of
 * those assertions is already satisfied part-way through typing: "clamp" matches after just "cl".
 * So Enter can be delivered while CodeMirror is still recomputing completions for the final
 * keystroke, at which point there is no *active* completion and `acceptCompletion` quietly does
 * nothing — Enter falls through, the document still reads "cla", and the next assertion fails on
 * a state that looks inexplicable. The shorter the discriminating prefix, the wider that window,
 * which is why the "cla" case flaked and the longer ones (e.g. "averageAud") only latently did.
 *
 * Waiting for the intended option to be the *selected* one closes it: selection is the settled
 * end state of a completion cycle, and it's precisely what a real user sees highlighted before
 * they press Enter. This is stronger than the assertion it replaces, not weaker — it additionally
 * pins that our option is the one CodeMirror actually defaults to.
 *
 * Use this rather than a bare `keyboard.press('Enter')` after typing into an expression editor.
 * Where several real options match a prefix and the intended one is *not* the default selection,
 * click the option directly instead (see calculations-editor.spec.ts's "or tr" step).
 */
export async function acceptCompletion(
  page: Page,
  tooltip: Locator,
  editorContent: Locator,
  optionName: RegExp
): Promise<void> {
  const option = tooltip.getByRole('option', { name: optionName });

  for (let attempt = 0; attempt < 3; attempt++) {
    await expect(option).toHaveAttribute('aria-selected', 'true');

    const before = (await editorContent.textContent()) ?? '';
    await page.keyboard.press('Enter');

    // Waiting for selection is necessary but not sufficient: a debounce cycle can still land
    // between the assertion and the keypress, leaving Enter to no-op again. The only signal that
    // actually means "accepted" is the document changing, so verify that and retry the benign
    // race rather than failing on a state that reads as inexplicable ("the tooltip was open, the
    // right option was selected, and Enter did nothing").
    try {
      await expect(editorContent).not.toHaveText(before, { timeout: 1_500 });
      return;
    } catch {
      // fall through and try again
    }
  }

  throw new Error(`Autocomplete option ${optionName} never applied after three attempts.`);
}
