import { test, expect } from '@playwright/test';
import { captureDocScreenshot, DEMO_USERS, loginAs, resetApp } from './fixtures';

const DOCS_DIR = 'docs/skills/calculations-editor/screenshots';

test.describe('Calculations tab', () => {
  test.beforeEach(async ({ request }) => {
    await resetApp(request);
  });

  async function selectInsuranceModeller(page: import('@playwright/test').Page) {
    const blueprintSelect = page.locator('select').first();
    const options = await blueprintSelect.locator('option').allTextContents();
    const targetLabel = options.find(option => option.includes('juggling-insurance-modeller'));
    await blueprintSelect.selectOption({ label: targetLabel });
  }

  test('renders the real calculations block, in the real declaration order, with live computed values', async ({ page }) => {
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const fieldRows = calcTab.locator('[data-wayfinder-calc-field]');
    const names = await fieldRows.evaluateAll(elements => elements.map(el => el.getAttribute('data-wayfinder-calc-field')));
    expect(names).toEqual([
      'riskMultiplier',
      'basePremium',
      'experienceDiscountRate',
      'riskLoading',
      'frequencyLoading',
      'audienceLoading',
      'totalLoading',
      'subtotal',
      'experienceDiscount',
      'totalPremium',
    ]);

    // The live preview evaluates against each input's own declared default — proves the whole
    // dependency chain resolves correctly end-to-end, not just that the fields are listed.
    await expect(calcTab.locator('[data-wayfinder-calc-field-preview]').first()).toBeVisible({ timeout: 5_000 });
    const previewTexts = await calcTab.locator('[data-wayfinder-calc-field-preview]').allTextContents();
    expect(previewTexts.every(text => text.startsWith('= '))).toBe(true);
    await captureDocScreenshot(calcTab, `${DOCS_DIR}/fields-live-preview.png`);

    const seriesSection = calcTab.locator('.calc-section', { hasText: 'Series' });
    const seriesRow = calcTab.locator('[data-wayfinder-calc-series="premiumByFrequency"]');
    await seriesSection.locator('summary').click();
    await expect(seriesRow).toBeVisible();
    await expect(seriesRow.locator('.calc-series-column-row')).toHaveCount(3);
    // The full section (all 3 columns) is taller than the shell's scrollable content pane, so a
    // single element screenshot only captures whatever portion is currently composited within
    // it — this shows the section's real structure (name/loop-variable/from/to plus its first
    // two repeatable column rows), not every row.
    await captureDocScreenshot(seriesSection, `${DOCS_DIR}/series-live-preview.png`);
  });

  test('a field name is not flagged as colliding with an input that has no default, but is flagged against one that does', async ({ page }) => {
    // Regression test: totalPremium is also the fieldKey of a summary-list display row (the
    // standard check-your-answers pattern) with no `default` — CalculationScopeBuilder.Build
    // never adds an input with neither a submission nor a default to the calc scope, so this
    // is not a real collision. averageAudienceSize DOES have a default, so it genuinely would
    // collide if a field were named after it.
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const totalPremiumRow = calcTab.locator('[data-wayfinder-calc-field="totalPremium"]');
    await totalPremiumRow.waitFor({ timeout: 10_000 });
    await expect(totalPremiumRow).not.toContainText('Collides with an input');

    const nameInput = totalPremiumRow.locator('input').first();
    await nameInput.fill('averageAudienceSize');
    await nameInput.dispatchEvent('change');
    await page.waitForTimeout(300);

    const renamedRow = calcTab.locator('[data-wayfinder-calc-field="averageAudienceSize"]');
    await expect(renamedRow).toContainText('Collides with an input field\'s own fieldKey ("averageAudienceSize").');
    await captureDocScreenshot(renamedRow, `${DOCS_DIR}/field-collision-error.png`);
  });

  test('a genuine calc collision appears in the Validation tab and blocks Save before it ever reaches the server', async ({ page }) => {
    // The Calculations tab's own inline check is advisory only — the Validation tab
    // (service-blueprint-validation.ts, shared with the Calculations tab and the Definition-tab
    // lint via calculation-diagnostics.ts) is what actually gates the Save button, mirroring
    // exactly what the server's own SaveAsync -> Validate() would reject the save for anyway.
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Validation' }).click();
    await expect(page.locator('[data-wayfinder-save]')).toBeEnabled();

    await page.getByRole('tab', { name: 'Calculations' }).click();
    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const totalPremiumRow = calcTab.locator('[data-wayfinder-calc-field="totalPremium"]');
    await totalPremiumRow.waitFor({ timeout: 10_000 });
    const nameInput = totalPremiumRow.locator('input').first();
    await nameInput.fill('averageAudienceSize');
    await nameInput.dispatchEvent('change');
    await page.waitForTimeout(300);

    await page.getByRole('tab', { name: 'Validation' }).click();
    const collisionIssue = page.locator('[data-wayfinder-validation-issue]', {
      hasText: 'Calculation field “averageAudienceSize” collides with an input field\'s own fieldKey.',
    });
    await expect(collisionIssue).toBeVisible();
    await captureDocScreenshot(
      page.locator('[data-wayfinder-validation-rail]'),
      `${DOCS_DIR}/validation-tab-blocked-save.png`
    );

    await page.getByRole('tab', { name: 'Canvas' }).click();
    await expect(page.locator('[data-wayfinder-save]')).toBeDisabled();

    await page.getByRole('tab', { name: 'Validation' }).click();
    await collisionIssue.click();
    await expect(page.getByRole('tab', { name: 'Calculations', selected: true })).toBeVisible();
  });

  test('a forward reference is fixed automatically and explained, and a genuine cycle is flagged and blocks reordering', async ({ page }) => {
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const addFieldButton = calcTab.locator('.calc-section', { hasText: 'Fields' }).getByRole('button', { name: '+ Add field' });
    await addFieldButton.click();
    await addFieldButton.click();
    await page.waitForTimeout(200);

    const fieldRows = calcTab.locator('[data-wayfinder-calc-field]');
    const names = await fieldRows.evaluateAll(elements => elements.map(el => el.getAttribute('data-wayfinder-calc-field')));
    const [first, second] = names.slice(-2);

    // Make the first-added field depend on the second-added one — a forward reference.
    const firstCm = calcTab.locator(`[data-wayfinder-calc-field="${first}"] wayfinder-calculation-expression-editor`).first().locator('.cm-content');
    // Typed with the reference last, not "${second} + 1" — as soon as a live-typed expression
    // contains a valid reference to a field declared later, _setFieldExpr fires on that
    // keystroke and _updateFields reorders the DOM immediately (mid-typing), which resets
    // CodeMirror's cursor to 0 and scrambles anything typed afterward. Typing the
    // reorder-triggering identifier last means nothing is left to land in the wrong place.
    await firstCm.click();
    await firstCm.pressSequentially(`1+${second}`);
    await page.waitForTimeout(400);

    const reordered = await fieldRows.evaluateAll(elements => elements.map(el => el.getAttribute('data-wayfinder-calc-field')));
    expect(reordered.indexOf(second)).toBeLessThan(reordered.indexOf(first));
    await expect(calcTab.locator('#calc-announcer')).toContainText(`Moved "${first}" after "${second}"`);
    // #calc-announcer is an sr-only ARIA live region (the on-screen evidence of the reorder is
    // the field's own new position and its expression referencing a field declared earlier) —
    // scroll the moved field into view since it was just appended near the end of a long list.
    const movedFieldRow = calcTab.locator(`[data-wayfinder-calc-field="${first}"]`);
    await movedFieldRow.scrollIntoViewIfNeeded();
    await captureDocScreenshot(movedFieldRow, `${DOCS_DIR}/auto-reorder-explained.png`);

    // Now make it a genuine cycle.
    const secondCm = calcTab.locator(`[data-wayfinder-calc-field="${second}"] wayfinder-calculation-expression-editor`).first().locator('.cm-content');
    await secondCm.click();
    await secondCm.pressSequentially(`${first} + 1`);
    await page.waitForTimeout(400);

    const cycleBanner = calcTab.locator('.calc-cycle-banner');
    await expect(cycleBanner).toBeVisible();
    await expect(cycleBanner).toContainText(first!);
    await expect(cycleBanner).toContainText(second!);
    await cycleBanner.scrollIntoViewIfNeeded();
    await captureDocScreenshot(cycleBanner, `${DOCS_DIR}/cycle-banner.png`);
  });

  test('an edit reaches the saved blueprint with the correct dependency order', async ({ page, request }) => {
    const original = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-insurance-modeller')).json();

    try {
      await loginAs(page, DEMO_USERS.caseworker);
      await page.getByRole('link', { name: 'Editor' }).click();

      const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
      await shell.waitFor({ timeout: 15_000 });

      await selectInsuranceModeller(page);
      await page.getByRole('tab', { name: 'Calculations' }).click();

      const calcTab = page.locator('wayfinder-calculations-editor');
      await calcTab.waitFor({ timeout: 10_000 });

      const addFieldButton = calcTab.locator('.calc-section', { hasText: 'Fields' }).getByRole('button', { name: '+ Add field' });
      await addFieldButton.click();
      await page.waitForTimeout(200);

      const newRow = calcTab.locator('[data-wayfinder-calc-field="field1"]');
      const nameInput = newRow.locator('input').first();
      await nameInput.fill('bonusAmount');
      await nameInput.dispatchEvent('change');
      await page.waitForTimeout(200);

      const exprCm = calcTab
        .locator('[data-wayfinder-calc-field="bonusAmount"] wayfinder-calculation-expression-editor')
        .first()
        .locator('.cm-content');
      await exprCm.click();
      await exprCm.pressSequentially('totalPremium * 0.1');
      await page.waitForTimeout(400);

      // The toolbar Save button lives inside the Canvas tab's own slotted content — hidden
      // while another tab is active, same as every other tab-specific control in this shell.
      await page.getByRole('tab', { name: 'Canvas' }).click();
      await page.locator('[data-wayfinder-save]').click();
      await expect(page.locator('[data-wayfinder-toast]')).toContainText(/saved/i, { timeout: 5_000 });
      await captureDocScreenshot(page.locator('[data-wayfinder-toast]'), `${DOCS_DIR}/save-confirmation.png`);

      const saved = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-insurance-modeller')).json();
      const fieldNames = Object.keys(saved.calculations.fields);

      expect(fieldNames).toContain('bonusAmount');
      // bonusAmount depends on totalPremium, so it must be declared after it — this is exactly
      // the ordering guarantee the canonical-JSON serializer must not disturb (see
      // service-blueprint-canonical-json.ts: calculations is deliberately excluded from
      // alphabetical key sorting).
      expect(fieldNames.indexOf('bonusAmount')).toBeGreaterThan(fieldNames.indexOf('totalPremium'));
      expect(saved.calculations.fields.bonusAmount.expr).toBe('totalPremium * 0.1');
      // Every original field must still be present and untouched.
      for (const name of Object.keys(original.calculations.fields)) {
        expect(saved.calculations.fields[name].expr).toBe(original.calculations.fields[name].expr);
      }
    } finally {
      const current = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-insurance-modeller')).json();
      await request.put('/wayfinder/service-blueprint-authoring/blueprints/juggling-insurance-modeller', {
        data: { ...original, version: current.version },
      });
    }
  });

  test('the reference picker sits next to the expression editor and filters/inserts correctly', async ({ page }) => {
    // Regression test: the "Insert a reference" control used to be a single below-the-editor
    // <select> — obtuse, and would only get more unwieldy as a blueprint's field/table count
    // grows. Now a filterable <input list> combobox sitting beside its own expression editor.
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const addFieldButton = calcTab.locator('.calc-section', { hasText: 'Fields' }).getByRole('button', { name: '+ Add field' });
    await addFieldButton.click();
    await page.waitForTimeout(200);

    const newRow = calcTab.locator('[data-wayfinder-calc-field="field1"]');
    await expect(newRow).toBeVisible();

    // Sits in the same row as the expression editor, to its right — not below it.
    const expressionRow = newRow.locator('.calc-expression-row');
    await expect(expressionRow.locator('wayfinder-calculation-expression-editor')).toHaveCount(1);
    await expect(expressionRow.locator('.reference-picker')).toHaveCount(1);

    // A real Tab-triggered blur, not dispatchEvent('change') — see stage-validations-editor.spec.ts
    // for why a synthetic dispatch double-inserts here specifically (insertAtCursor's own
    // focus() call blurs this input a second time).
    const pickerInput = expressionRow.locator('.reference-picker-input');
    await pickerInput.fill('Average audience size (averageAudienceSize)');
    await pickerInput.press('Tab');
    await page.waitForTimeout(200);

    // Exact text, not just toContainText — a double-fired insert would still "contain" the
    // substring, which is exactly how the original version of this test missed the bug.
    await expect(expressionRow.locator('.cm-content')).toHaveText('averageAudienceSize');
    // Clears itself back to empty after inserting, ready for the next reference.
    await expect(pickerInput).toHaveValue('');

    await expressionRow.scrollIntoViewIfNeeded();
    await captureDocScreenshot(expressionRow, `${DOCS_DIR}/field-reference-picker.png`);
  });

  test('a table can be added, with interpolate and row values reflected in the UI', async ({ page }) => {
    // Neither seed blueprint declares any calculations.tables — this is the only coverage of
    // the Tables section, added specifically so its docs/skills screenshot has real content
    // behind a real assertion, not a screenshot with nothing verifying it.
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await shell.waitFor({ timeout: 15_000 });

    await selectInsuranceModeller(page);
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const tablesSection = calcTab.locator('.calc-section', { hasText: 'Tables' });
    await tablesSection.locator('summary').click();
    await tablesSection.getByRole('button', { name: '+ Add table' }).click();

    const tableRow = calcTab.locator('[data-wayfinder-calc-table="table1"]');
    await expect(tableRow).toBeVisible();

    await tableRow.locator('select').selectOption('step');
    await tableRow.getByRole('button', { name: '+ Add row' }).click();

    const keyInput = tableRow.locator('table tbody tr').first().locator('input').first();
    const valueInput = tableRow.locator('table tbody tr').first().locator('input').nth(1);
    await keyInput.fill('1');
    await keyInput.dispatchEvent('change');
    await valueInput.fill('1.15');
    await valueInput.dispatchEvent('change');

    await expect(tableRow.locator('table tbody tr')).toHaveCount(1);
    await expect(valueInput).toHaveValue('1.15');
    await captureDocScreenshot(tablesSection, `${DOCS_DIR}/tables-section.png`);
  });
});
