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

  test('inline autocomplete lists a real field once, never duplicated by its own summary-list echoes', async ({ page }) => {
    // Regression test: applicantName is captured once (your-details, a real input) and echoed
    // read-only on two summary-lists (declaration, under-review) — collectStageInputFields
    // recursed into those DataDisplay children too, offering it three times as a suggestion.
    // (An earlier side-panel picker version of this UI also had a layout bug — a picker column
    // next to a long, non-wrapping expression box would visually collide with it — which inline
    // autocomplete sidesteps structurally: it needs no side column at all.)
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', {
      timeout: 15_000,
    });
    await page.getByRole('tab', { name: 'Calculations' }).click();

    const calcTab = page.locator('wayfinder-calculations-editor');
    await calcTab.waitFor({ timeout: 10_000 });

    const addFieldButton = calcTab.locator('.calc-section', { hasText: 'Fields' }).getByRole('button', { name: '+ Add field' });
    await addFieldButton.click();
    await page.waitForTimeout(200);

    const newRow = calcTab.locator('[data-wayfinder-calc-field="field1"]');
    await expect(newRow).toBeVisible();

    const editor = newRow.locator('wayfinder-calculation-expression-editor');
    const cm = editor.locator('.cm-content');
    await cm.click();
    await cm.pressSequentially('full');

    const tooltip = editor.locator('.cm-tooltip-autocomplete');
    await expect(tooltip).toBeVisible();
    await expect(tooltip.getByRole('option', { name: /applicantName/ })).toHaveCount(1);

    await tooltip.scrollIntoViewIfNeeded();
    await captureDocScreenshot(newRow, `${DOCS_DIR}/field-autocomplete-no-duplicates.png`);
  });

  test('inline autocomplete filters as you type and inserts the selected field, in place of a side-panel picker', async ({ page }) => {
    // The Calculations tab previously placed an "Insert a reference" control beside/below each
    // expression box — a plain <select>, then a filterable picker column. Both got unwieldy as a
    // blueprint's field/table count grows, and the picker column specifically collided with a
    // long expression (this editor is deliberately single-line, no wrapping). Inline autocomplete
    // needs no extra layout space and scales the same way regardless of expression length.
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

    const editor = newRow.locator('wayfinder-calculation-expression-editor');
    const cm = editor.locator('.cm-content');
    await cm.click();
    await cm.pressSequentially('averageAud');

    const tooltip = editor.locator('.cm-tooltip-autocomplete');
    await expect(tooltip).toBeVisible();
    await expect(tooltip).toContainText('averageAudienceSize');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);

    // Exact text, not just toContainText — a double-inserted value would still "contain" the
    // substring, which is exactly how an earlier version of this test missed a real double-fire
    // bug in the side-panel picker this replaced.
    await expect(cm).toHaveText('averageAudienceSize');

    await newRow.scrollIntoViewIfNeeded();
    await captureDocScreenshot(newRow, `${DOCS_DIR}/field-autocomplete.png`);
  });

  test('inline autocomplete covers the whole calculation language, not just field references', async ({ page }) => {
    // IntelliSense for the grammar itself: every built-in function (as a snippet — accepting
    // "clamp" inserts "clamp(value, lo, hi)" with the first argument selected, ready to overtype
    // and Tab through, same as a real IDE) and every keyword/boolean literal, not only
    // blueprint-specific field/table names.
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

    const editor = newRow.locator('wayfinder-calculation-expression-editor');
    const cm = editor.locator('.cm-content');
    const tooltip = editor.locator('.cm-tooltip-autocomplete');

    await test.step('a function completes as a snippet with tab-stops over its own arguments', async () => {
      await cm.click();
      await cm.pressSequentially('cla');
      await expect(tooltip).toBeVisible();
      await expect(tooltip).toContainText('clamp');
      await page.keyboard.press('Enter');
      await page.waitForTimeout(200);
      await expect(cm).toHaveText('clamp(value, lo, hi)');

      // The first placeholder ("value") is selected on insertion — typing overtypes it, not
      // appends after it.
      await page.keyboard.type('5');
      await expect(cm).toHaveText('clamp(5, lo, hi)');

      // Tab moves to the next placeholder ("lo"), which is likewise selected.
      await page.keyboard.press('Tab');
      await page.keyboard.type('10');
      await expect(cm).toHaveText('clamp(5, 10, hi)');
    });

    await test.step('a keyword completes too, not only functions and fields', async () => {
      await cm.click();
      await page.keyboard.press('End');
      await cm.pressSequentially(' or tr');
      await expect(tooltip).toBeVisible();
      // "tr" alone also substring-matches this blueprint's own "experienceDiscountRate" field
      // (…cou**ntr**ate) — click the exact keyword option rather than trust Enter picks whichever
      // one CM6 highlights by default among several real matches. The option's label/detail spans
      // have no whitespace between them in the DOM, so the accessible name concatenates with no
      // space ("truebooleanliteral") — anchor on the start only, not a \b word boundary.
      const trueOption = tooltip.getByRole('option', { name: /^true/ });
      await expect(trueOption).toBeVisible();
      await trueOption.click();
      await page.waitForTimeout(200);
      await expect(cm).toHaveText('clamp(5, 10, hi) or true');
    });
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
