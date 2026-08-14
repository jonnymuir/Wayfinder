import { expect, test } from '@playwright/test';
import { captureDocScreenshot } from './support/canvas-helpers';

const DOCS_DIR = 'docs/skills/canvas-editor/screenshots';

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

// A support-system-call action's own dedicated editor — cascading support-system/capability
// pickers, then one field per the chosen capability's declared inputs (reusing the same
// field-ref-aware rendering a component's own properties use). See docs/guides/support-systems.md
// and the real "insurer-validation" stage in Wayfinder.ReferenceApp/service-blueprints/
// juggling-licence.json, which the story fixture mirrors in shape.
test.describe('Support-system-call action editor', () => {
  test('cascading support-system/capability pickers render capability inputs bound to real fields, and the outcomes hint names the declared outcomes', async ({
    page,
  }) => {
    await page.goto(storyUrl('service-blueprint-editor-step-inspector--support-system-call-action-configuration'));

    const actionEditor = page.locator('wayfinder-step-inspector wayfinder-stage-action-editor');
    await expect(actionEditor).toBeVisible({ timeout: 10_000 });

    const supportSystemSelect = actionEditor.locator('[data-wayfinder-support-system-select="0"]');
    await expect(supportSystemSelect).toHaveValue('safetynet-underwriting');

    const capabilitySelect = actionEditor.locator('[data-wayfinder-support-system-capability-select="0"]');
    await expect(capabilitySelect).toHaveValue('validate-risk-assessment');

    // The required "File" input is bound to the real riskAssessment field captured on the earlier
    // "Risk assessment" stage — proving supportSystemFieldReferences is genuinely blueprint-wide,
    // not scoped to "insurer-validation" itself (which captures no fields of its own). IDs follow
    // wayfinder-stage-action-editor.ts's idPrefix convention (`support-system-call-${index}-${inputKey}`).
    const fileSelect = actionEditor.locator('#support-system-call-0-file');
    await expect(fileSelect).toHaveValue('riskAssessment');

    const notesSelect = actionEditor.locator('#support-system-call-0-notes');
    await expect(notesSelect).toHaveValue('riskMitigationNotes');

    await expect(actionEditor).toContainText('approved, rejected');

    await actionEditor.scrollIntoViewIfNeeded();
    await captureDocScreenshot(page.locator('wayfinder-step-inspector'), `${DOCS_DIR}/support-system-call-action-editor.png`);
  });

  test('changing the support system resets the capability and any bound inputs', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-step-inspector--support-system-call-action-configuration'));

    const actionEditor = page.locator('wayfinder-step-inspector wayfinder-stage-action-editor');
    await expect(actionEditor).toBeVisible({ timeout: 10_000 });

    const capabilitySelect = actionEditor.locator('[data-wayfinder-support-system-capability-select="0"]');
    await expect(capabilitySelect).toHaveValue('validate-risk-assessment');

    const supportSystemSelect = actionEditor.locator('[data-wayfinder-support-system-select="0"]');
    await supportSystemSelect.selectOption('');

    await expect(capabilitySelect).toHaveValue('');
    await expect(capabilitySelect).toBeDisabled();
    await expect(actionEditor).toContainText('Choose a support system.');
  });
});
