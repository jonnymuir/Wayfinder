import { expect, test } from '@playwright/test';

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

async function expectStageSelectionDetails(
  page: import('@playwright/test').Page,
  stageKey: string
) {
  const stage = page.locator(`[data-wayfinder-stage="${stageKey}"]`);
  await expect(stage).toBeVisible();
  await expect(stage).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator(`[data-wayfinder-stage-detail="${stageKey}"]`)).toBeVisible();
}

async function pressRedoShortcut(page: import('@playwright/test').Page) {
  const isMac = process.platform === 'darwin';
  await page.locator('wayfinder-service-blueprint-editor').evaluate((element, mac) => {
    element.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'z',
      bubbles: true,
      composed: true,
      shiftKey: true,
      metaKey: mac,
      ctrlKey: !mac,
    }));
  }, isMac);
}

test.describe('ServiceBlueprint editor undo and redo', () => {
  test('toolbar buttons and keyboard shortcuts replay stage title edits', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-wayfinder-undo]')).toBeDisabled();
    await expect(page.locator('[data-wayfinder-redo]')).toBeDisabled();

    await page.locator('[data-wayfinder-stage="declaration"]').dblclick();
    const titleInput = page.locator('[data-wayfinder-stage-title]');
    await expect(titleInput).toHaveValue('Declaration');
    await titleInput.fill('Declaration updated');
    await titleInput.press('Tab');

    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declaration updated');
    await expect(page.locator('[data-wayfinder-history-status]')).toContainText('1 change available to undo');
    await expect(page.locator('[data-wayfinder-undo]')).toBeEnabled();
    await expect(page.locator('[data-wayfinder-redo]')).toBeDisabled();

    await page.locator('[data-wayfinder-undo]').click();
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declaration');
    await expect(titleInput).toHaveValue('Declaration');
    await expect(page.locator('[data-wayfinder-redo]')).toBeEnabled();

    await pressRedoShortcut(page);
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declaration updated');
    await expect(titleInput).toHaveValue('Declaration updated');
    await expect(page.locator('[data-wayfinder-redo]')).toBeDisabled();
  });

  test('stage and transition mutations can be undone and redone from the host editor', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-wayfinder-add-stage]').click();
    const createStageDialog = page.locator('[data-wayfinder-create-stage-dialog]');
    await expect(createStageDialog).toBeVisible();
    await createStageDialog.locator('[data-wayfinder-create-stage-title]').fill('Site visit');
    await createStageDialog.locator('[data-wayfinder-create-stage-key]').fill('site-visit');
    await createStageDialog.locator('[data-wayfinder-create-stage-queue]').fill('reviewer');
    await createStageDialog.locator('[data-wayfinder-create-stage-type]').selectOption('review');
    await createStageDialog.getByRole('button', { name: 'Create stage' }).click();
    await expect(createStageDialog).toBeHidden();

    await expectStageSelectionDetails(page, 'site-visit');
    await page.locator('[data-wayfinder-undo]').click();
    await expect(page.locator('[data-wayfinder-stage="site-visit"]')).toHaveCount(0);
    await page.locator('[data-wayfinder-redo]').click();
    await expectStageSelectionDetails(page, 'site-visit');

    // Route mutations are now scoped to the gateway inspector's outgoing-routes panel
    // (Slice 3b.1: transition creation/edit was removed from the canvas; routes are
    // only authored from existing gateway transitions). Switch fixtures to exercise
    // route label edits and route deletion undo/redo on the gateway story.
    await page.goto(storyUrl('service-blueprint-editor-editor-host--gateway-representation'));
    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-wayfinder-gateway="review-split"]').click();
    const labelInput = page
      .locator('[data-wayfinder-gateway-route] [data-wayfinder-route-label]')
      .first();
    await expect(labelInput).toBeVisible();
    const originalLabel = await labelInput.inputValue();

    await labelInput.fill('continue applicant branch (edited)');
    await labelInput.press('Tab');
    await expect(
      page
        .locator('[data-wayfinder-gateway-route] [data-wayfinder-route-label]')
        .first()
    ).toHaveValue('continue applicant branch (edited)');

    await page.locator('[data-wayfinder-undo]').click();
    await expect(
      page
        .locator('[data-wayfinder-gateway-route] [data-wayfinder-route-label]')
        .first()
    ).toHaveValue(originalLabel);
    await pressRedoShortcut(page);
    await expect(
      page
        .locator('[data-wayfinder-gateway-route] [data-wayfinder-route-label]')
        .first()
    ).toHaveValue('continue applicant branch (edited)');

    const routesBefore = await page.locator('[data-wayfinder-gateway-route]').count();
    await page
      .locator('[data-wayfinder-gateway-route] [data-wayfinder-route-delete]')
      .first()
      .click();
    await expect(page.locator('[data-wayfinder-gateway-route]')).toHaveCount(routesBefore - 1);

    await page.locator('[data-wayfinder-undo]').click();
    await expect(page.locator('[data-wayfinder-gateway-route]')).toHaveCount(routesBefore);
    await pressRedoShortcut(page);
    await expect(page.locator('[data-wayfinder-gateway-route]')).toHaveCount(routesBefore - 1);
  });

  test('action adds, parameter edits, reorders, and deletes replay through history', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-wayfinder-stage="declaration"]').dblclick();
    // Scoped to the stage's OWN action editor — "declaration" now also has its own outgoing
    // route(s), each carrying a wayfinder-stage-action-editor of its own since a stage's routes
    // became fully editable in place, so an unscoped locator is ambiguous on this fixture.
    const stage = page.locator('wayfinder-stage-action-editor[subject-label="stage"]');
    const formDefinitionInput = stage.locator('[data-wayfinder-action-param="0-formDefinitionId"]');
    await expect(formDefinitionInput).toHaveValue('planning-declaration');
    await formDefinitionInput.fill('planning-declaration-v2');
    await expect(formDefinitionInput).toHaveValue('planning-declaration-v2');

    await stage.locator('[data-wayfinder-open-action-picker]').click();
    await stage.locator('[data-wayfinder-action-picker-option="notifications.send-sms"]').click();
    await stage.locator('[data-wayfinder-action-picker-add]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(2);

    await stage.locator('[data-wayfinder-stage-action="1"]').focus();
    await page.keyboard.press('Alt+ArrowUp');
    await expect(stage.locator('[data-wayfinder-stage-action="0"] .action-title')).toContainText('Send SMS');

    await stage.locator('[data-wayfinder-stage-action-remove="0"]').click();
    const deleteDialog = stage.locator('[data-wayfinder-delete-action-dialog]');
    await expect(deleteDialog).toBeVisible();
    await stage.locator('[data-wayfinder-delete-action-confirm]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(1);

    await page.locator('[data-wayfinder-undo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(2);
    await expect(stage.locator('[data-wayfinder-stage-action="0"] .action-title')).toContainText('Send SMS');

    await page.locator('[data-wayfinder-undo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action="1"] .action-title')).toContainText('Send SMS');

    await page.locator('[data-wayfinder-undo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(1);

    await page.locator('[data-wayfinder-undo]').click();
    await expect(formDefinitionInput).toHaveValue('planning-declaration');

    await page.locator('[data-wayfinder-redo]').click();
    await expect(formDefinitionInput).toHaveValue('planning-declaration-v2');
    await page.locator('[data-wayfinder-redo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(2);
    await page.locator('[data-wayfinder-redo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action="0"] .action-title')).toContainText('Send SMS');
    await page.locator('[data-wayfinder-redo]').click();
    await expect(stage.locator('[data-wayfinder-stage-action]')).toHaveCount(1);
  });
});
