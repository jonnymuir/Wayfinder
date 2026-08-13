import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Proves the reference app's own integration wiring — not the editor component itself, which
// Wayfinder.Editor.Client already covers with its own Playwright/Storybook suite. What's novel
// here is that Wayfinder.Editor's packaged demo page (service-blueprint-editor.html) talks to
// a `/mockapp/service-blueprints/*` contract this host implements against the same live
// ServiceBlueprintAuthoringService the REST/MCP authoring surface uses — see Program.cs's
// comment above those routes.
test.describe('Service blueprint editor', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('the editor loads the seeded juggling-licence blueprint via the packaged demo page', async ({ page }) => {
    await loginAs(page, DEMO_USERS.caseworker);
    await page.getByRole('link', { name: 'Editor' }).click();

    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', {
      timeout: 15_000
    });

    const selector = page.locator('select.service-blueprint-selector');
    await expect(selector).toContainText('Apply for a licence to hold a juggling event');

    await expect(page.locator('wayfinder-service-blueprint-editor')).toHaveAttribute('blueprint-key', 'juggling-licence');
  });

  test('the authoring API and MCP surfaces the editor is backed by are reachable and list the seed', async ({ request }) => {
    const apiResponse = await request.get('/wayfinder/service-blueprint-authoring/blueprints');
    expect(apiResponse.ok()).toBeTruthy();
    const blueprints = await apiResponse.json();
    expect(blueprints).toContainEqual(
      expect.objectContaining({ definitionKey: 'juggling-licence' })
    );

    const mockAppResponse = await request.get('/mockapp/service-blueprints/juggling-licence');
    expect(mockAppResponse.ok()).toBeTruthy();
    const blueprint = await mockAppResponse.json();
    expect(blueprint.definitionKey).toBe('juggling-licence');
    expect(blueprint.stages.map((s: { stageKey: string }) => s.stageKey)).toEqual([
      'your-details',
      'event-details',
      'risk-assessment',
      'declaration',
      // Reviewing and deciding are deliberately separate caseworker stages, so that sending a
      // risk assessment to the insurer can be *required* between them — see
      // docs/guides/support-systems.md.
      'under-review',
      'caseworker-decision',
      // The automation queue's own stage — carries the support-system-call action that sends
      // the risk assessment to SafetyNet Underwriting.
      'insurer-validation',
      'approved',
      'rejected'
    ]);
  });

  // The properties panel's schema-driven component add/edit UI (see Wayfinder.Editor.Client's
  // own Storybook/Playwright suite for the UI behaviour itself) fetches this live from whichever
  // host it's talking to — proving it's actually reachable here, backed by this host's real
  // ComponentTypeRegistry, is what makes that UI usable against this reference app at all.
  test('the component type catalog is reachable and reflects this host\'s own registered types', async ({ request }) => {
    const response = await request.get('/wayfinder/service-blueprint-authoring/component-types');
    expect(response.ok()).toBeTruthy();
    const descriptors = await response.json();

    expect(descriptors).toContainEqual(expect.objectContaining({ discriminator: 'text', category: 'Input' }));
    // "rating" is Wayfinder.ReferenceApp's own toolkit-extension component (see
    // Services/CustomComponents.cs) — never built into Wayfinder itself. Its presence here is
    // the whole point: an editor UI driven by this endpoint needs no code change to offer it.
    expect(descriptors).toContainEqual(expect.objectContaining({ discriminator: 'rating', category: 'Input' }));
  });

  // A real regression: the properties panel's schema-driven edit form (component-property-editor.ts)
  // read/wrote a component's field values under the raw C# CLR property name (e.g. "FieldKey")
  // instead of the real wire-format camelCase key ("fieldKey") — every field appeared blank on
  // open, and an edit silently wrote to a property the runtime never reads, so Save appeared to
  // do nothing. Fixed server-side (ComponentDescriptor.cs's PropertyNameJsonConverter converts
  // once, at the JSON boundary) rather than by asking every client call site to convert itself.
  // Invisible to Wayfinder.Editor.Client's own Storybook suite, which uses a hand-built fixture
  // catalog rather than a live server's real descriptor output — this integration test, against
  // this host's actual seeded data, is what actually catches it.
  test('editing an existing component in the properties panel populates real field values and the save reaches the live blueprint', async ({
    page,
    request,
  }) => {
    // ProcessManagerEngine.ResetAll (called by resetApp's beforeEach) only clears instances, not
    // definitions saved through the authoring API — restore the original afterwards so later
    // specs in this shared, single-worker process see the unmodified seed (same pattern as
    // custom-component.spec.ts).
    const original = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();

    try {
      await loginAs(page, DEMO_USERS.caseworker);
      await page.getByRole('link', { name: 'Editor' }).click();

      const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
      await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', {
        timeout: 15_000,
      });

      await page.locator('.react-flow').getByText('Your details', { exact: true }).click();

      const inspector = page.locator('wayfinder-step-inspector');
      await expect(inspector.getByText('Fieldset', { exact: false }).first()).toBeVisible({ timeout: 5_000 });

      // Expand the top-level fieldset, then the "Email address" child within it — a native
      // <details>/<summary>, so the child expands via its own summary row, not a separate button.
      await inspector.getByRole('button', { name: 'Edit' }).first().click();
      const emailSummary = inspector.locator('.child-editor summary', { hasText: 'Email address' });
      await emailSummary.scrollIntoViewIfNeeded();
      await emailSummary.click();

      // Scoped via the CSS :has() pseudo-class against `.field-item-meta` (the component's raw
      // discriminator, "email" — stable and never user-edited), not `.child-item`'s whole text
      // content, not the *label* ("Email address" — this very test edits it below, so a locator
      // anchored on the label breaks the moment it changes and the next action on it hangs
      // retrying forever), and not Playwright's own `.filter({has})` (which doesn't reliably
      // pierce this app's nested custom-element shadow DOM, confirmed live — it returns zero
      // matches here even though the target element genuinely exists). A reference-aware dropdown
      // elsewhere in the tree (e.g. "Full name"'s own "Conditional on field" select) can
      // legitimately contain the string "Email address" too, as one of its own options — and a
      // closed native <details> still has its content in the DOM, just hidden — so a looser
      // `hasText` match on the whole `.child-item` grabs the wrong sibling.
      const emailChild = inspector.locator('.child-item:has(.field-item-meta:text-is("email"))');
      const fieldKeyInput = emailChild.locator('input').first();
      const labelInput = emailChild.locator('input').nth(1);

      // The seed's real values (service-blueprints/juggling-licence.json) — not blank, and not
      // literally "FieldKey"/"Label" (the un-converted CLR property names the bug would have read).
      await expect(fieldKeyInput).toHaveValue('applicantEmail');
      await expect(labelInput).toHaveValue('Email address');

      await labelInput.fill('Email address (edited)');
      await labelInput.dispatchEvent('change');

      await page.locator('[data-wayfinder-save]').click();
      await expect(page.locator('[data-wayfinder-toast]')).toContainText(/saved/i, { timeout: 5_000 });

      const saved = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      const yourDetails = saved.stages.find((s: { stageKey: string }) => s.stageKey === 'your-details');
      const email = yourDetails.components[0].children.find((c: { type: string }) => c.type === 'email');
      expect(email.label).toBe('Email address (edited)');
      // The edit must have landed on the real "label" property, not created a stray "Label" one.
      expect(email.Label).toBeUndefined();
    } finally {
      const current = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      await request.put(`/wayfinder/service-blueprint-authoring/blueprints/juggling-licence`, {
        data: { ...original, version: current.version },
      });
    }
  });

  test('reference-aware property fields render as populated dropdowns and the choices reach the saved blueprint', async ({
    page,
    request,
  }) => {
    // Same restore-afterwards pattern as the test above — this shares the same seeded
    // definition/process across a single-worker run.
    const original = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();

    try {
      await loginAs(page, DEMO_USERS.caseworker);
      await page.getByRole('link', { name: 'Editor' }).click();

      const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
      await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', {
        timeout: 15_000,
      });

      await page.locator('.react-flow').getByText('Your details', { exact: true }).click();

      const inspector = page.locator('wayfinder-step-inspector');
      await inspector.getByRole('button', { name: 'Edit' }).first().click();

      // "Email address" — set "Conditional on field" via the dropdown (not free text) to its
      // sibling "applicantName", proving the dropdown is populated with real sibling field keys
      // and that a selection actually reaches the saved component.
      const emailSummary = inspector.locator('.child-editor summary', { hasText: 'Email address' });
      await emailSummary.scrollIntoViewIfNeeded();
      await emailSummary.click();
      // Scoped via CSS :has() against `.field-item-meta` (the stable discriminator, "email") —
      // see the comment on the equivalent locator in the test above for why both a looser
      // `.child-item` `hasText` match, an exact match against the *label*, and Playwright's own
      // `.filter({has})` are all unsafe/unreliable here.
      const emailChild = inspector.locator('.child-item:has(.field-item-meta:text-is("email"))');
      const conditionalOnSelect = emailChild.locator('select[id$="conditionalOn"]').first();
      await expect(conditionalOnSelect.locator('option', { hasText: 'applicantName' })).toHaveCount(1);
      await conditionalOnSelect.selectOption({ label: 'Full name (applicantName)' });

      // "Full name" — insert the "Letters only" regex preset into Pattern, proving the preset
      // select actually writes into the field's real saved value.
      const fullNameSummary = inspector.locator('.child-editor summary', { hasText: 'Full name' });
      await fullNameSummary.scrollIntoViewIfNeeded();
      await fullNameSummary.click();
      const fullNameChild = inspector.locator('.child-item:has(.field-item-meta:text-is("text"))');
      const presetSelect = fullNameChild.locator('select[id$="-pattern-preset"]').first();
      await presetSelect.selectOption({ label: 'Letters only' });

      await page.locator('[data-wayfinder-save]').click();
      await expect(page.locator('[data-wayfinder-toast]')).toContainText(/saved/i, { timeout: 5_000 });

      const saved = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      const yourDetails = saved.stages.find((s: { stageKey: string }) => s.stageKey === 'your-details');
      const children = yourDetails.components[0].children;
      const email = children.find((c: { type: string }) => c.type === 'email');
      const fullName = children.find((c: { fieldKey: string }) => c.fieldKey === 'applicantName');

      expect(email.conditionalOn).toBe('applicantName');
      expect(fullName.pattern).toBe('^[A-Za-z]+$');
    } finally {
      const current = await (await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence')).json();
      await request.put(`/wayfinder/service-blueprint-authoring/blueprints/juggling-licence`, {
        data: { ...original, version: current.version },
      });
    }
  });
});
