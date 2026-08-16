import { expect, test } from '@playwright/test';

function storyUrl(storyId: string): string {
  return `/iframe.html?id=${storyId}&viewMode=story`;
}

// Slice 3b.1: route creation/editing relocated to the inspector's outgoing-routes panel. The drag
// handle, keyboard 't' shortcut, and the dedicated create-transition dialog have all been retired.
//
// The route-visibility ("showWhen") behaviour below replaced an always/event/guard
// route-condition UI that looked functional but was never evaluated anywhere in the engine and —
// because of a client/server wire-key mismatch — didn't even survive a save (see
// docs/guides/calculation-language.md's "Route visibility" section). ShowWhen only has an effect
// on a stage's own routes, never a gateway's own (a Split fans out to every route regardless, a
// Join selects by matching the arriving trigger) — so the editor for it is offered only on a
// stage-owned route, and deliberately absent from a gateway-owned one.
test.describe('Route visibility (showWhen)', () => {
  test("author editing a stage's outgoing route can set the expression that determines when it's offered", async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--gateway-representation'));

    const editor = page.locator('wayfinder-service-blueprint-editor');
    await expect(editor).toBeVisible({ timeout: 10_000 });

    await page.getByRole('button', { name: 'Expand outline panel' }).click();
    const outline = editor.locator('wayfinder-service-blueprint-outline');
    // A route's own full editor block only ever renders inside a gateway's inspector view — a
    // stage's own "Outgoing routes" panel is read-only summary text (see
    // _renderGatewayOutgoingRoutes vs. _renderStage). decision-join is a Join gateway, so this
    // is its "Incoming routes" panel; reviewer-assessment -> decision-join is still stage-owned
    // (fromGateway is unset regardless of which gateway's panel it's viewed from), so showWhen
    // is offered here exactly the same as it would be from any other angle on this same route.
    await outline.locator('[data-wayfinder-outline-gateway="decision-join"]').click();

    const inspector = editor.locator('wayfinder-step-inspector');
    await expect(inspector.locator('[data-wayfinder-gateway-detail="decision-join"]')).toBeVisible();

    const routeBlock = inspector.locator('[data-wayfinder-gateway-route]', { hasText: 'confirm review' });
    await expect(routeBlock).toBeVisible();

    const showWhenEditor = routeBlock.locator('wayfinder-calculation-expression-editor');
    await expect(showWhenEditor).toBeVisible();
    const showWhenContent = showWhenEditor.locator('.cm-content');
    await showWhenContent.click();
    await showWhenContent.pressSequentially('readyForReview');
    await page.keyboard.press('Escape'); // dismiss autocomplete without accepting a suggestion
    await showWhenContent.blur();

    // (a) Inspector reflects the updated expression.
    await expect(showWhenContent).toHaveText('readyForReview');

    // (b) Underlying route showWhen is updated in the service blueprint model.
    const updatedShowWhen = await inspector.evaluate(node => {
      const el = node as unknown as {
        serviceBlueprint: { stages?: Array<{ stateKey: string; routes?: Array<{ target: string; showWhen?: string }> }> } | null;
      };
      for (const stage of (el.serviceBlueprint?.stages ?? [])) {
        if (stage.stateKey !== 'reviewer-assessment') continue;
        for (const route of (stage.routes ?? [])) {
          if (route.target === 'decision-join') return route.showWhen ?? null;
        }
      }
      return null;
    });
    expect(updatedShowWhen).toBe('readyForReview');

    // (c) The polite live region announced the update.
    const announcement = await inspector.evaluate(node => {
      const announcer = (node as HTMLElement).shadowRoot?.getElementById('inspector-announcer');
      return announcer?.textContent?.trim() ?? '';
    });
    expect(announcement).toMatch(/route visibility condition updated/i);
  });

  test("a gateway's own outgoing route offers no showWhen editor — it would silently have no effect there", async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--gateway-representation'));

    const editor = page.locator('wayfinder-service-blueprint-editor');
    await expect(editor).toBeVisible({ timeout: 10_000 });

    await page.getByRole('button', { name: 'Expand outline panel' }).click();
    const outline = editor.locator('wayfinder-service-blueprint-outline');
    await outline.locator('[data-wayfinder-outline-gateway="review-split"]').click();

    const inspector = editor.locator('wayfinder-step-inspector');
    await expect(inspector.locator('[data-wayfinder-gateway-detail="review-split"]')).toBeVisible();

    const routeBlock = inspector.locator('[data-wayfinder-route-target="reviewer-assessment"]');
    await expect(routeBlock).toBeVisible();
    await expect(routeBlock.locator('wayfinder-calculation-expression-editor')).toHaveCount(0);
  });
});

// Regression: a stage's own "Outgoing routes" panel used to render a read-only summary line for
// every route it owns — no way back into a route's full editor (target/role/showWhen/actions)
// once it existed, unless it happened to also be reachable as a Join gateway's own "Incoming
// routes". Most routes never are: start-request -> review-split targets an ordinary Split
// pass-through, the shape almost every "Continue" button in a real service blueprint uses, and a
// Split's own "Outgoing routes" panel only ever shows its OWN downstream routes, never routes
// INTO it. So this specific, extremely common route shape had no editable surface anywhere.
test.describe("A stage's own routes are fully editable from the stage's own panel", () => {
  test('selecting the stage shows the full route editor for each of its own routes, not a read-only summary', async ({ page }) => {
    await page.goto(storyUrl('service-blueprint-editor-editor-host--gateway-representation'));

    const editor = page.locator('wayfinder-service-blueprint-editor');
    await expect(editor).toBeVisible({ timeout: 10_000 });
    await page.getByRole('button', { name: 'Expand outline panel' }).click();

    const outline = editor.locator('wayfinder-service-blueprint-outline');
    await outline.locator('[data-wayfinder-outline-stage="start-request"]').click();

    const inspector = editor.locator('wayfinder-step-inspector');
    await expect(inspector.locator('[data-wayfinder-stage-detail="start-request"]')).toBeVisible();

    const routeBlock = inspector.locator('[data-wayfinder-route-target="review-split"]');
    await expect(routeBlock).toBeVisible();
    // The full editor, not the old breadcrumb-only summary — a real, editable field.
    await expect(routeBlock.locator('[data-wayfinder-route-label]')).toBeVisible();

    const label = routeBlock.locator('[data-wayfinder-route-label]');
    await label.fill('begin request');
    await label.dispatchEvent('change');
    await expect(inspector.locator('[data-wayfinder-route-target="review-split"] .gateway-route-title')).toHaveText('begin request');
  });

  test("a route targeting a gateway shows an honest read-only readout, not a native select silently defaulted to the wrong stage", async ({ page }) => {
    // Before this fix: the "Target stage" <select> only ever lists real stages, so a route whose
    // target is a gateway (review-split) matched none of its <option>s, and — since nothing was
    // marked selected — the browser's own native fallback silently highlighted the FIRST stage in
    // the list instead. That's not just a cosmetic glitch: an author who "reselected" what looked
    // like the current value would genuinely retarget the route to that wrong stage.
    await page.goto(storyUrl('service-blueprint-editor-editor-host--gateway-representation'));

    const editor = page.locator('wayfinder-service-blueprint-editor');
    await expect(editor).toBeVisible({ timeout: 10_000 });
    await page.getByRole('button', { name: 'Expand outline panel' }).click();

    const outline = editor.locator('wayfinder-service-blueprint-outline');
    await outline.locator('[data-wayfinder-outline-stage="start-request"]').click();

    const inspector = editor.locator('wayfinder-step-inspector');
    const routeBlock = inspector.locator('[data-wayfinder-route-target="review-split"]');
    await expect(routeBlock).toBeVisible();

    await expect(routeBlock.locator('[data-wayfinder-route-target-select]')).toHaveCount(0);
    const readout = routeBlock.locator('[data-wayfinder-route-target-gateway]');
    await expect(readout).toBeVisible();
    await expect(readout).toContainText('Review split');
  });
});
