import { test, expect } from '@playwright/test';
import { captureDocScreenshot, DEMO_USERS, loginAs, resetApp } from './fixtures';

const DOCS_DIR = 'docs/skills/calculations-editor/screenshots';

// StageDefinition.Validations — declarative cross-field business rules checked before a stage can
// advance, the engine-native alternative to a host writing custom C# validation code (see
// docs/guides/calculation-language.md's "Stage validations" section). juggling-licence.json (the
// reference app's default-loaded blueprint) carries a real one on its "risk-assessment" stage —
// exercised here rather than a synthetic fixture, so this is the same worked example the docs and
// the Wayfinder engine tests use.
test.describe('Validations section (Calculations tab)', () => {
  test.beforeEach(async ({ request }) => {
    await resetApp(request);
  });

  async function openCalculationsTabOnJugglingLicence(page: import('@playwright/test').Page) {
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', {
      timeout: 15_000,
    });

    await page.getByRole('tab', { name: 'Calculations' }).click();
    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });
    return calcTab;
  }

  test('renders the real cross-stage validation rule from the juggling-licence seed', async ({ page }) => {
    const calcTab = await openCalculationsTabOnJugglingLicence(page);

    const validationsSection = calcTab.locator('.calc-section', { hasText: 'Validations' });
    await validationsSection.locator('summary').click();

    const rule = calcTab.locator('[data-wayfinder-calc-validation="risk-assessment-0"]');
    await expect(rule).toBeVisible();

    await expect(rule.locator('input').first()).toHaveValue('risk-mitigation-evidence-required');
    await expect(rule.locator('select').first()).toHaveValue('riskMitigationNotes');
    await expect(rule.locator('textarea')).toHaveValue(/measurable detail/);

    // The "when" expression editor references hasDangerousProps — captured two stages earlier
    // (event-details), proving this is a genuinely cross-stage reference, not a same-stage one.
    const whenEditor = rule.locator('wayfinder-calculation-expression-editor').first();
    await expect(whenEditor.locator('.cm-content')).toContainText('hasDangerousProps');

    await rule.scrollIntoViewIfNeeded();
    await captureDocScreenshot(rule, `${DOCS_DIR}/stage-validation-rule.png`);
  });

  test('the reference picker sits next to its own expression editor and inserts into the right one', async ({ page }) => {
    // Regression test for a real UX complaint: a single shared "insert a reference" dropdown
    // below both when/rule, targeting "whichever was last focused", was ambiguous — this proves
    // each expression editor now has its own dedicated, correctly-wired picker.
    const calcTab = await openCalculationsTabOnJugglingLicence(page);

    const validationsSection = calcTab.locator('.calc-section', { hasText: 'Validations' });
    await validationsSection.locator('summary').click();

    const eventDetailsStage = calcTab.locator('.calc-validations-stage', { hasText: 'About the event' });
    await eventDetailsStage.getByRole('button', { name: '+ Add validation rule' }).click();
    await page.waitForTimeout(200);

    const newRule = eventDetailsStage.locator('[data-wayfinder-calc-validation]').last();
    await expect(newRule).toBeVisible();

    const whenRow = newRule.locator('.calc-expression-row').nth(0);
    const ruleRow = newRule.locator('.calc-expression-row').nth(1);

    // Sits to the right of its expression editor, not below both — same row, two children.
    await expect(whenRow.locator('wayfinder-calculation-expression-editor')).toHaveCount(1);
    await expect(whenRow.locator('.reference-picker')).toHaveCount(1);

    // A real Tab-triggered blur, not a synthetic dispatchEvent('change') — insertAtCursor()
    // focuses the CodeMirror view, which blurs this input; a synthetic dispatch doesn't clear
    // the browser's own internal "value changed since focus" bookkeeping the way a real native
    // change does, so that blur would then fire a second, genuinely native change and double the
    // insert. A real user typing + tabbing away never hits this (the browser only tracks one
    // genuine edit), so this is the faithful way to exercise it.
    const hasDangerousPropsLabel = 'This act involves fire, knives, or other dangerous props (hasDangerousProps)';
    const whenPickerInput = whenRow.locator('.reference-picker-input');
    await whenPickerInput.fill(hasDangerousPropsLabel);
    await whenPickerInput.press('Tab');
    await page.waitForTimeout(200);

    // Landed in the "when" editor specifically, not "rule" (proves per-editor wiring, not just
    // "inserts somewhere") — and the picker input clears itself back to empty after inserting.
    await expect(whenRow.locator('.cm-content')).toHaveText('hasDangerousProps');
    await expect(ruleRow.locator('.cm-content')).not.toContainText('hasDangerousProps');
    await expect(whenPickerInput).toHaveValue('');

    const jugglerCountLabel = 'Number of jugglers taking part (jugglerCount)';
    const rulePickerInput = ruleRow.locator('.reference-picker-input');
    await rulePickerInput.fill(jugglerCountLabel);
    await rulePickerInput.press('Tab');
    await page.waitForTimeout(200);

    await expect(ruleRow.locator('.cm-content')).toHaveText('jugglerCount');
    // The earlier "when" insert is untouched by inserting into "rule" afterwards.
    await expect(whenRow.locator('.cm-content')).toHaveText('hasDangerousProps');

    await newRule.scrollIntoViewIfNeeded();
    await captureDocScreenshot(newRule, `${DOCS_DIR}/stage-validation-reference-pickers.png`);
  });

  test('a new validation rule can be authored and reaches the saved blueprint', async ({ page, request }) => {
    const original = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();

    try {
      const calcTab = await openCalculationsTabOnJugglingLicence(page);

      const validationsSection = calcTab.locator('.calc-section', { hasText: 'Validations' });
      await validationsSection.locator('summary').click();

      const eventDetailsStage = calcTab.locator('.calc-validations-stage', { hasText: 'About the event' });
      await eventDetailsStage.getByRole('button', { name: '+ Add validation rule' }).click();
      await page.waitForTimeout(200);

      const newRule = eventDetailsStage.locator('[data-wayfinder-calc-validation]').last();
      await expect(newRule).toBeVisible();

      const codeInput = newRule.locator('input').first();
      await codeInput.fill('at-least-one-juggler');
      await codeInput.dispatchEvent('change');

      // Deliberately references no input field: most inputs in this seed have no declared
      // default (only hasDangerousProps and riskMitigationNotes do), and validate_service_blueprint
      // can only resolve a field with a real submission or a default (see docs/guides/
      // calculation-language.md) — this test is about the authoring flow round-tripping
      // correctly, not about exercising that separate, already-covered gotcha.
      const ruleCm = newRule.locator('wayfinder-calculation-expression-editor').nth(1).locator('.cm-content');
      await ruleCm.click();
      await ruleCm.pressSequentially('true');
      await page.waitForTimeout(300);

      const messageInput = newRule.locator('textarea');
      await messageInput.fill('At least one juggler is required.');
      await messageInput.dispatchEvent('change');
      await page.waitForTimeout(200);

      await page.getByRole('tab', { name: 'Canvas' }).click();
      await page.locator('[data-wayfinder-save]').click();
      await expect(page.locator('[data-wayfinder-toast]')).toContainText(/saved/i, { timeout: 5_000 });

      const saved = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      const eventDetailsSaved = saved.stages.find((s: { stageKey: string }) => s.stageKey === 'event-details');
      const savedRule = eventDetailsSaved.validations.find((r: { code: string }) => r.code === 'at-least-one-juggler');

      expect(savedRule).toBeTruthy();
      expect(savedRule.rule).toBe('true');
      expect(savedRule.message).toBe('At least one juggler is required.');

      // The pre-existing risk-assessment rule must survive untouched.
      const riskAssessmentSaved = saved.stages.find((s: { stageKey: string }) => s.stageKey === 'risk-assessment');
      expect(riskAssessmentSaved.validations).toHaveLength(1);
      expect(riskAssessmentSaved.validations[0].code).toBe('risk-mitigation-evidence-required');
    } finally {
      const current = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      await request.put('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence', {
        data: { ...original, version: current.version },
      });
    }
  });
});
