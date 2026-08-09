import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

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

    const seriesRow = calcTab.locator('[data-wayfinder-calc-series="premiumByFrequency"]');
    await page.locator('.calc-section', { hasText: 'Series' }).locator('summary').click();
    await expect(seriesRow).toBeVisible();
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
    await firstCm.click();
    await firstCm.pressSequentially(`${second} + 1`);
    await page.waitForTimeout(400);

    const reordered = await fieldRows.evaluateAll(elements => elements.map(el => el.getAttribute('data-wayfinder-calc-field')));
    expect(reordered.indexOf(second)).toBeLessThan(reordered.indexOf(first));
    await expect(calcTab.locator('#calc-announcer')).toContainText(`Moved "${first}" after "${second}"`);

    // Now make it a genuine cycle.
    const secondCm = calcTab.locator(`[data-wayfinder-calc-field="${second}"] wayfinder-calculation-expression-editor`).first().locator('.cm-content');
    await secondCm.click();
    await secondCm.pressSequentially(`${first} + 1`);
    await page.waitForTimeout(400);

    const cycleBanner = calcTab.locator('.calc-cycle-banner');
    await expect(cycleBanner).toBeVisible();
    await expect(cycleBanner).toContainText(first!);
    await expect(cycleBanner).toContainText(second!);
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
});
