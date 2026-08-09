import { expect, test } from '@playwright/test';
import { captureDocScreenshot } from './support/canvas-helpers';
// TODO Slice E: re-cert after gateway-pill rendering + simulation reshape. See .squad/decisions/inbox/copilot-slice-d-close-out.md.
// The three fixme tests below still fail if un-skipped as-is: [data-wayfinder-simulation-start]
// needs the Simulation tab selected first (not done by these), and — confirmed live — the
// transition buttons stay disabled past that point, so the happy-path/rejection/blocker
// walkthroughs need real re-certification against the current UI, not just a tab-activation
// fix. The test below intentionally covers only the "start a simulation" state, which does
// work today, for docs/skills/simulation-tab/ — it does not attempt to fix the rest.

const DOCS_DIR = 'docs/skills/simulation-tab/screenshots';

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

test.describe('ServiceBlueprint editor path simulation', () => {
  test('starting a simulation shows the initial stage and history', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.getByRole('tab', { name: 'Simulation' }).click();

    const panel = page.locator('[data-wayfinder-simulation-panel]');
    await page.locator('[data-wayfinder-simulation-start]').click();

    await expect(page.locator('[data-wayfinder-simulation-initial-stage]')).toHaveText('Declaration');
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Declaration');
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toHaveAttribute('data-wayfinder-stage-simulation-current', 'true');
    await expect(page.locator('[data-wayfinder-simulation-history]')).toContainText('Declaration');
    await captureDocScreenshot(panel, `${DOCS_DIR}/simulation-started.png`);
  });

  test.fixme('starts from the initial stage, advances through the happy path, and highlights the graph path', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-simulation-start]').click();

    await expect(page.locator('[data-wayfinder-simulation-initial-stage]')).toHaveText('Declaration');
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Declaration');
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toHaveAttribute('data-wayfinder-stage-simulation-current', 'true');
    await expect(page.locator('[data-wayfinder-simulation-history]')).toContainText('Declaration');

    await page.locator('[data-wayfinder-simulation-transition="0"]').click();
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Application Form');
    await expect(page.locator('[data-wayfinder-transition="0"]')).toHaveAttribute('data-wayfinder-transition-simulation-path', 'true');

    await page.locator('[data-wayfinder-simulation-transition="1"]').click();
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Check your answers');

    await page.locator('[data-wayfinder-simulation-transition="2"]').click();
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Application submitted');
    await expect(page.locator('[data-wayfinder-simulation-stop-reason="terminal"]')).toContainText('end stage');
    await expect(page.locator('[data-wayfinder-stage="submitted"]')).toHaveAttribute('data-wayfinder-stage-simulation-current', 'true');
    await expect(page.locator('[data-wayfinder-simulation-history]')).toContainText('Application submitted');
  });

  test.fixme('supports a rejection path and keeps the breadcrumb in sync', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--simulation-branches'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-simulation-start]').click();
    await page.locator('[data-wayfinder-simulation-transition="0"]').click();
    await page.locator('[data-wayfinder-simulation-transition="1"]').click();

    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Reviewer decision');
    await expect(page.locator('[data-wayfinder-simulation-transition="3"]')).toContainText('reject');

    await page.locator('[data-wayfinder-simulation-transition="3"]').click();
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Application rejected');
    await expect(page.locator('[data-wayfinder-simulation-stop-reason="terminal"]')).toContainText('end stage');
    await expect(page.locator('[data-wayfinder-simulation-history]')).toContainText('Reviewer decision');
    await expect(page.locator('[data-wayfinder-simulation-history]')).toContainText('Application rejected');
  });

  test.fixme('blocks invalid transitions and stops automatically at waiting stages', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--simulation-blockers'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-simulation-start]').click();
    await page.locator('[data-wayfinder-simulation-transition="0"]').click();
    await page.locator('[data-wayfinder-simulation-transition="1"]').click();

    const blockedTransition = page.locator('[data-wayfinder-simulation-transition="3"]');
    await expect(blockedTransition).toBeDisabled();
    await expect(page.locator('[data-wayfinder-simulation-blocker="3"]')).toContainText('missing target stage');

    await page.locator('[data-wayfinder-simulation-transition="4"]').click();
    await expect(page.locator('[data-wayfinder-simulation-current-stage]')).toHaveText('Checks pending');
    await expect(page.locator('[data-wayfinder-simulation-stop-reason="waiting"]')).toContainText('waiting stage');
    await expect(page.locator('[data-wayfinder-stage="checks-pending"]')).toHaveAttribute('data-wayfinder-stage-simulation-current', 'true');
  });
});
