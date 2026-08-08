import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// Proves the component-catalog extensibility work end to end: Wayfinder.ReferenceApp defines
// and registers a genuinely new component type ("rating") that Wayfinder itself has never heard
// of — see Wayfinder.ReferenceApp/Services/CustomComponents.cs. This spec authors it into the
// live juggling-licence blueprint via the real authoring API (exercising Phase 2's descriptor-
// driven property validation and queue-capability check along the way), then drives it through
// the actual citizen-facing HTML journey, proving the host's own GovUkComponentRenderer.RegisterField
// override — not a generic fallback — is what renders it.
test.describe('Toolkit extension: a genuinely new component type', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('a custom "rating" component round-trips through the authoring API and renders/captures a real answer', async ({
    page,
    request,
  }) => {
    const getResponse = await request.get('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence');
    expect(getResponse.ok()).toBeTruthy();
    const original = await getResponse.json();

    const modified = structuredClone(original);
    const eventDetailsStage = modified.stages.find((s: { stageKey: string }) => s.stageKey === 'event-details');
    eventDetailsStage.components[0].children.push({
      type: 'rating',
      fieldKey: 'confidenceRating',
      label: 'How confident are you in running this event safely?',
      hint: 'A toolkit-extension component, defined outside Wayfinder itself.',
      required: false,
    });
    const declarationStage = modified.stages.find((s: { stageKey: string }) => s.stageKey === 'declaration');
    declarationStage.components[0].children.push({
      type: 'rating',
      fieldKey: 'confidenceRating',
      label: 'Confidence in running this event safely',
      required: false,
      changeStateKey: 'event-details',
    });

    // The real authoring API — validate_service_blueprint's descriptor-driven property checks
    // and queue-capability check (Phase 2) run here, against a type only ever registered at
    // this host's own startup, not built into Wayfinder.
    const putResponse = await request.put('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence', {
      data: modified,
    });
    expect(putResponse.ok()).toBeTruthy();
    const saveOutcome = await putResponse.json();
    expect(saveOutcome.status, JSON.stringify(saveOutcome)).toBe('Saved');
    // The restore below must target the version this save just produced, not the one `original`
    // was loaded at — the store's optimistic-concurrency check would otherwise reject it as a
    // conflict (exactly the guarantee ServiceBlueprintAuthoringService.SaveAsync exists to give).
    const restorePayload = { ...original, version: saveOutcome.newVersion };

    try {
      await loginAs(page, DEMO_USERS.applicant);
      await page.getByLabel('Full name').fill('Alex Applicant');
      await page.getByLabel('Email address').fill('alex@example.test');
      await page.getByRole('button', { name: 'Continue' }).click();

      await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
      // Real govuk-frontend radios markup from the host's own RegisterField override — a type
      // the engine has never heard of, rendered as more than the generic fallback would allow.
      await expect(page.getByText('How confident are you in running this event safely?')).toBeVisible();
      await expect(page.getByText('A toolkit-extension component, defined outside Wayfinder itself.')).toBeVisible();
      await page.getByLabel('Confident', { exact: true }).check();

      await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
      await page.getByLabel('Day').fill('1');
      await page.getByLabel('Month').fill('9');
      await page.getByLabel('Year').fill('2026');
      await page.getByLabel('Number of jugglers taking part').fill('12');
      await page.getByRole('button', { name: 'Continue' }).click();

      await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
      await page.getByRole('button', { name: 'Continue' }).click();

      await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
      // The submitted radio value ("4", the 4th of 5 options) survives the round trip through
      // the engine and back out to a rendered page — proof this is genuinely captured, not just
      // accepted and discarded.
      const row = page.locator('.govuk-summary-list__row', { hasText: 'Confidence in running this event safely' });
      await expect(row.locator('.govuk-summary-list__value')).toHaveText('4');
    } finally {
      // ProcessManagerEngine.ResetAll (called by resetApp's beforeEach) only clears instances,
      // not definitions updated via the authoring API — restore the original so later specs in
      // this shared, single-worker process see the unmodified seed.
      const restoreResponse = await request.put('/wayfinder/service-blueprint-authoring/blueprints/juggling-licence', {
        data: restorePayload,
      });
      expect(restoreResponse.ok()).toBeTruthy();
    }
  });
});
