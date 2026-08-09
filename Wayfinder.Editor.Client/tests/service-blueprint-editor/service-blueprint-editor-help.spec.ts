import { expect, test } from '@playwright/test';
import { SERVICE_BLUEPRINT_SHORTCUT_GROUPS } from '../../src/service-blueprint-editor/editor-shortcuts';
import { captureDocScreenshot } from './support/canvas-helpers';
// TODO Slice E: re-cert after gateway-pill rendering + simulation reshape. See .squad/decisions/inbox/copilot-slice-d-close-out.md.

const DOCS_DIR = 'docs/skills/help-tab/screenshots';

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

async function dispatchEditorShortcut(
  page: import('@playwright/test').Page,
  detail: { key: string; shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean }
) {
  await page.locator('wayfinder-service-blueprint-editor').evaluate((element, shortcut) => {
    element.dispatchEvent(new KeyboardEvent('keydown', {
      key: shortcut.key,
      bubbles: true,
      composed: true,
      shiftKey: shortcut.shiftKey ?? false,
      ctrlKey: shortcut.ctrlKey ?? false,
      metaKey: shortcut.metaKey ?? false,
    }));
  }, detail);
}

function editorModifier() {
  const isMac = process.platform === 'darwin';
  return { ctrlKey: !isMac, metaKey: isMac };
}

test.describe('ServiceBlueprint editor help and shortcut reference', () => {
  test('help button and F1 open the shortcut guide, and the list stays aligned with the exported shortcut map', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    const helpButton = page.locator('[data-wayfinder-help]');
    await helpButton.focus();
    await helpButton.click();

    const dialog = page.locator('[data-wayfinder-shortcut-dialog]');
    await expect(dialog).toBeVisible();

    for (const group of SERVICE_BLUEPRINT_SHORTCUT_GROUPS) {
      const groupSection = dialog.locator(`[data-wayfinder-shortcut-group="${group.id}"]`);
      await expect(groupSection).toContainText(group.title);
      for (const shortcut of group.shortcuts) {
        const row = groupSection.locator(`[data-wayfinder-shortcut="${shortcut.id}"]`);
        await expect(row).toContainText(shortcut.command);
        await expect(row).toContainText(shortcut.context);
        for (const label of shortcut.labels) {
          await expect(row).toContainText(label);
        }
      }
    }

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(helpButton).toBeFocused();

    await dispatchEditorShortcut(page, { key: 'F1' });
    await expect(dialog).toBeVisible();
  });

  test.fixme('save and redo shortcuts stay discoverable and wired to the host editor commands', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-wayfinder-stage="declaration"]').dblclick();

    const titleInput = page.locator('[data-wayfinder-stage-title]');
    await titleInput.fill('Declaration shortcut check');
    await titleInput.press('Tab');

    await dispatchEditorShortcut(page, { key: 's', ...editorModifier() });
    await expect(page.locator('[data-wayfinder-toast]')).toContainText('ServiceBlueprint saved and published.');
    await expect(page.locator('[data-wayfinder-save]')).toHaveAttribute('aria-keyshortcuts', 'Control+S Meta+S');
    await expect(page.locator('[data-wayfinder-help]')).toHaveAttribute('aria-keyshortcuts', 'F1');

    await page.locator('[data-wayfinder-undo]').click();
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declaration');

    await dispatchEditorShortcut(page, { key: 'y', ...editorModifier() });
    await expect(page.locator('[data-wayfinder-stage="declaration"]')).toContainText('Declaration shortcut check');
    await expect(page.locator('[data-wayfinder-redo]')).toHaveAttribute(
      'aria-keyshortcuts',
      'Control+Y Meta+Y Control+Shift+Z Meta+Shift+Z'
    );
  });

  test('empty service blueprints show getting-started guidance and still expose help', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--empty-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-wayfinder-empty-state="graph"]')).toContainText('Start building your service blueprint');
    await expect(page.locator('[data-wayfinder-empty-state="graph"]')).toContainText('Add the next stage before you branch');

    const dialog = page.locator('[data-wayfinder-shortcut-dialog]');
    // The story play() function clicks the help button and opens the dialog as part of
    // Storybook's own interaction test. Wait for it to finish, dismiss it, then verify
    // the button opens it again so we are testing a real user interaction here.
    await expect(dialog).toBeVisible({ timeout: 5_000 });
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();

    await page.locator('[data-wayfinder-help]').click();
    await expect(dialog).toBeVisible();
  });

  // The tests above cover the keyboard-shortcut dialog (opened via the toolbar help button /
  // F1) — a different feature from the Help *tab* itself (<wayfinder-help-panel>, slot="help"
  // in wayfinder-service-blueprint-editor.ts), which had no test coverage at all until now.
  test('the Help tab shows keyboard shortcuts, quick tips, and a getting-started guide', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--planning-service-blueprint'));

    await expect(page.locator('wayfinder-service-blueprint-editor')).toBeVisible({ timeout: 10_000 });
    await page.getByRole('tab', { name: 'Help' }).click();

    const helpPanel = page.locator('wayfinder-help-panel');
    await expect(helpPanel).toBeVisible();
    await expect(helpPanel).toContainText('Service Blueprint editor help');

    // Same shortcut data the toolbar dialog above reads from, so this can never drift from it.
    const firstGroup = SERVICE_BLUEPRINT_SHORTCUT_GROUPS[0];
    await expect(helpPanel).toContainText(firstGroup.title);
    await expect(helpPanel).toContainText(firstGroup.shortcuts[0].command);

    await expect(helpPanel).toContainText('Quick tips');
    await expect(helpPanel).toContainText('Getting started');
    await captureDocScreenshot(helpPanel, `${DOCS_DIR}/help-tab.png`);
  });
});
