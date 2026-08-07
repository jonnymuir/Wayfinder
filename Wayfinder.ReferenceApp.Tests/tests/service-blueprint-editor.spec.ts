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
      'under-review',
      'approved',
      'rejected'
    ]);
  });
});
