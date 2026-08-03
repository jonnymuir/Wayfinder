import { expect, test } from '@playwright/test';
// TODO Slice E: re-cert after gateway-pill rendering + simulation reshape. See .squad/decisions/inbox/copilot-slice-d-close-out.md.

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

async function pressEditorShortcut(
  page: import('@playwright/test').Page,
  key: 'c' | 'v'
) {
  const isMac = process.platform === 'darwin';
  await page.locator('wayfinder-service-blueprint-editor').evaluate((element, detail) => {
    element.dispatchEvent(new KeyboardEvent('keydown', {
      key: detail.key,
      bubbles: true,
      composed: true,
      metaKey: detail.isMac,
      ctrlKey: !detail.isMac,
    }));
  }, { key, isMac });
}

test.describe('ServiceBlueprint editor copy and paste', () => {
  test.fixme('stage copy and paste uses a new key, keeps transitions behind, and selects the pasted stage', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-stage="declaration"]').dblclick();
    await expect(page.locator('[data-wayfinder-stage-detail="declaration"]')).toBeVisible();

    await pressEditorShortcut(page, 'c');
    await expect(page.locator('[data-wayfinder-clipboard-state]')).toContainText('stage “Declaration” ready to paste');

    await page.locator('[data-wayfinder-stage="application-form"]').dblclick();
    await pressEditorShortcut(page, 'v');

    await expect(page.locator('[data-wayfinder-transition]')).toHaveCount(3);
    await expect(page.locator('[data-wayfinder-stage="declaration-copy"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-wayfinder-stage-detail="declaration-copy"]')).toBeVisible();
    await expect(page.locator('wayfinder-step-inspector')).toBeFocused();
    await expect(page.locator('[data-wayfinder-stage-detail="declaration-copy"]')).toContainText(
      'Add at least one outbound transition before publishing this stage.'
    );
  });

  test('action copy and paste works in the same stage and a different stage', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-stage="declaration"]').dblclick();
    await expect(page.locator('[data-wayfinder-stage-detail="declaration"]')).toBeVisible();

    const originalAction = page.locator('[data-wayfinder-stage-action="0"]');
    await originalAction.focus();
    await expect(originalAction).toHaveAttribute('data-wayfinder-action-selected', 'true');

    await page.locator('[data-wayfinder-copy]').click();
    await expect(page.locator('[data-wayfinder-toast]')).toContainText('Copied action Load the declaration form.');

    await pressEditorShortcut(page, 'v');
    await expect(page.locator('[data-wayfinder-stage-action]')).toHaveCount(2);
    await expect(page.locator('[data-wayfinder-stage-action="1"]')).toHaveAttribute('data-wayfinder-action-selected', 'true');
    await expect(page.locator('[data-wayfinder-action-param="1-formDefinitionId"]')).toHaveValue('planning-declaration');

    await page.locator('[data-wayfinder-stage="application-form"]').dblclick();
    await page.locator('[data-wayfinder-paste]').click();

    await expect(page.locator('[data-wayfinder-stage-detail="application-form"]')).toBeVisible();
    await expect(page.locator('[data-wayfinder-stage-action]')).toHaveCount(2);
    await expect(page.locator('[data-wayfinder-stage-action="1"]')).toHaveAttribute('data-wayfinder-action-selected', 'true');
    await expect(page.locator('[data-wayfinder-action-param="1-formDefinitionId"]')).toHaveValue('planning-declaration');
  });
});
