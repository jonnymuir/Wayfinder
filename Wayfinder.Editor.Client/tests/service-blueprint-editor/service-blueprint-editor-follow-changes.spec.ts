import { expect, type Page, test } from '@playwright/test';
import { captureDocScreenshot, VISUAL_VIEWPORT } from './support/canvas-helpers';

const STORY = '/iframe.html?id=service-blueprint-editor-editor-host--planning-service-blueprint&viewMode=story';

/** Saves through the editor's own source, as an AI agent or another person would: a new stage, and a renamed one. */
async function saveAsSomeoneElse(page: Page) {
  await page.locator('wayfinder-service-blueprint-editor').evaluate(async (element) => {
    const editor = element as HTMLElement & {
      blueprintKey: string;
      serviceBlueprintSource: {
        load(key: string): Promise<{ stages: Array<Record<string, unknown>> }>;
        save(key: string, blueprint: unknown): Promise<void>;
      };
    };
    const source = editor.serviceBlueprintSource;
    const blueprint = await source.load(editor.blueprintKey);
    const submitted = blueprint.stages.find((stage) => stage.stageKey === 'submitted');
    blueprint.stages.push({ ...submitted, stageKey: 'fee-check', displayName: 'Check the fee', routes: [] });
    const declaration = blueprint.stages.find((stage) => stage.stageKey === 'declaration');
    if (declaration) {
      declaration.displayName = 'Declare and sign';
    }
    await source.save(editor.blueprintKey, blueprint);
  });
}

async function openEditor(page: Page) {
  await page.setViewportSize(VISUAL_VIEWPORT);
  await page.clock.install();
  await page.goto(STORY);
  await expect(page.locator('[data-wayfinder-stage="declaration"]')).toBeVisible({ timeout: 10_000 });
}

test.describe('ServiceBlueprint editor following changes saved elsewhere', () => {
  test('with nothing unsaved, the canvas moves to the new version and shows what changed', async ({ page }) => {
    await openEditor(page);
    await page.locator('[data-wayfinder-stage="application-form"]').click();

    await saveAsSomeoneElse(page);
    await page.clock.fastForward(16_000);

    const notice = page.locator('[data-wayfinder-recent-changes]');
    await expect(notice).toContainText('Updated elsewhere, from version 1 to 2');
    await expect(notice).toContainText('1 stage added, 1 stage changed.');
    await expect(page.locator('[data-wayfinder-recent-changes-status]')).toHaveText(
      'Updated to version 2: 1 stage added, 1 stage changed.'
    );

    await expect(page.locator('[data-wayfinder-stage="fee-check"]')).toHaveAttribute('data-wayfinder-stage-change', 'added');
    await expect(page.locator('[data-wayfinder-stage="fee-check"]')).toContainText('New');
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toHaveAttribute('data-wayfinder-stage-change', 'changed');
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declare and sign');
    await expect(page.locator('[data-wayfinder-stage="submitted"]')).not.toHaveAttribute('data-wayfinder-stage-change', /.*/);

    await expect(page.locator('[data-wayfinder-stage="application-form"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-wayfinder-stale-service-blueprint-overlay]')).toHaveCount(0);
    await captureDocScreenshot(
      page.locator('wayfinder-service-blueprint-editor'),
      'docs/skills/canvas-editor/screenshots/changes-saved-elsewhere.png'
    );
  });

  test('dismissing the notice clears the highlights', async ({ page }) => {
    await openEditor(page);
    await saveAsSomeoneElse(page);
    await page.clock.fastForward(16_000);
    await expect(page.locator('[data-wayfinder-recent-changes]')).toBeVisible();

    await page.locator('[data-wayfinder-dismiss-recent-changes]').click();

    await expect(page.locator('[data-wayfinder-recent-changes]')).toHaveCount(0);
    await expect(page.locator('[data-wayfinder-stage="fee-check"]')).toBeVisible();
    await expect(page.locator('[data-wayfinder-stage="fee-check"]')).not.toHaveAttribute('data-wayfinder-stage-change', /.*/);
  });

  test('with unsaved work, the editor keeps it and goes read-only instead of moving', async ({ page }) => {
    await openEditor(page);
    await page.locator('[data-wayfinder-stage="application-form"]').dblclick();
    const title = page.locator('[data-wayfinder-stage-title]');
    await title.fill('Application form, my edit');
    await title.press('Tab');

    await saveAsSomeoneElse(page);
    await page.clock.fastForward(16_000);

    await expect(page.locator('[data-wayfinder-stale-service-blueprint-overlay]')).toBeVisible();
    await expect(page.locator('[data-wayfinder-stage="application-form"]')).toContainText('Application form, my edit');
    await expect(page.locator('[data-wayfinder-stage="fee-check"]')).toHaveCount(0);
    await expect(page.locator('[data-wayfinder-recent-changes]')).toHaveCount(0);
  });
});
