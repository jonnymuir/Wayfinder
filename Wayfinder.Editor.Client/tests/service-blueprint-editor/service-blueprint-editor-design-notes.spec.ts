import { expect, type Page, test } from '@playwright/test';
import { captureDocScreenshot, VISUAL_VIEWPORT } from './support/canvas-helpers';

const STORY = '/iframe.html?id=service-blueprint-editor-editor-host--planning-service-blueprint&viewMode=story';

type NoteAnchor = { kind: string; key?: string };

/** Works on the notes as an AI design partner does through the MCP tools: straight against the host's store. */
async function asDesignPartner(page: Page, action: 'add' | 'resolve', args: { anchor?: NoteAnchor; text: string }) {
  await page.locator('wayfinder-service-blueprint-editor').evaluate(
    async (element, { action, args }) => {
      const partner = { name: 'Design partner', kind: 'DesignPartner' };
      const editor = element as HTMLElement & {
        blueprintKey: string;
        serviceBlueprintSource: {
          designNotes: {
            list(key: string): Promise<Array<{ id: string }>>;
            add(key: string, anchor: unknown, text: string, author: unknown): Promise<unknown>;
            resolve(key: string, id: string, summary: string, author: unknown): Promise<unknown>;
          };
        };
      };
      const notes = editor.serviceBlueprintSource.designNotes;
      if (action === 'add') {
        await notes.add(editor.blueprintKey, args.anchor, args.text, partner);
      } else {
        const [first] = await notes.list(editor.blueprintKey);
        await notes.resolve(editor.blueprintKey, first.id, args.text, partner);
      }
    },
    { action, args }
  );
}

async function openEditor(page: Page) {
  await page.setViewportSize(VISUAL_VIEWPORT);
  await page.clock.install();
  await page.goto(STORY);
  await expect(page.locator('[data-wayfinder-stage="declaration"]')).toBeVisible({ timeout: 10_000 });
}

async function openSelectionNotes(page: Page) {
  const notes = page.locator('[data-wayfinder-selection-notes]');
  if ((await notes.getAttribute('open')) === null) {
    await notes.locator('summary').click();
  }
}

async function addNoteTo(page: Page, stageKey: string, text: string) {
  await page.locator(`[data-wayfinder-stage="${stageKey}"]`).click();
  await openSelectionNotes(page);
  const form = page.locator('[data-wayfinder-add-design-note]');
  await form.getByRole('textbox').fill(text);
  await form.getByRole('button', { name: 'Add note' }).click();
}

test.describe('ServiceBlueprint editor design notes', () => {
  test('the way to add a note stays on screen below a stage with a long inspector', async ({ page }) => {
    await openEditor(page);

    await page.locator('[data-wayfinder-stage="application-form"]').click();
    await openSelectionNotes(page);

    const form = page.locator('[data-wayfinder-add-design-note]');
    await expect(form.getByRole('textbox')).toBeInViewport();
    await expect(form.getByRole('button', { name: 'Add note' })).toBeInViewport();
  });

  test('the notes under Properties start closed, say how many are open, and stay open between selections', async ({ page }) => {
    await openEditor(page);
    await asDesignPartner(page, 'add', { anchor: { kind: 'Stage', key: 'declaration' }, text: 'Is a declaration needed?' });
    await page.clock.fastForward(6_000);

    await page.locator('[data-wayfinder-stage="declaration"]').click();

    const notes = page.locator('[data-wayfinder-selection-notes]');
    await expect(notes.locator('summary')).toHaveText('Notes (1 open)');
    await expect(notes.getByRole('textbox')).toBeHidden();

    await notes.locator('summary').click();
    await expect(notes.getByRole('textbox')).toBeVisible();

    await page.locator('[data-wayfinder-stage="application-form"]').click();
    await expect(notes.locator('summary')).toHaveText('Notes');
    await expect(notes.getByRole('textbox')).toBeVisible();
  });

  test('a note added to a selected stage is marked on the canvas and listed in Notes', async ({ page }) => {
    await openEditor(page);

    await addNoteTo(page, 'declaration', 'Is a declaration needed for renewals?');

    const stage = page.locator('[data-wayfinder-stage="declaration"]');
    await expect(stage).toHaveAttribute('data-wayfinder-stage-notes', '1');
    await expect(stage).toHaveAccessibleName(/1 open note$/);
    await expect(page.locator('[data-wayfinder-selection-notes]')).toContainText('Is a declaration needed for renewals?');
    await expect(page.locator('[data-wayfinder-tab-badge="notes"]')).toHaveText('1');

    await page.getByRole('tab', { name: /Notes/ }).click();
    const note = page.locator('[data-wayfinder-design-note]');
    await expect(note).toHaveCount(1);
    await expect(note).toContainText('Is a declaration needed for renewals?');
    await expect(note.locator('.design-note-anchor')).toContainText('Stage: ');
  });

  test('a question the design partner pins appears without a reload, and leads back to its stage', async ({ page }) => {
    await openEditor(page);

    await asDesignPartner(page, 'add', {
      anchor: { kind: 'Stage', key: 'application-form' },
      text: 'Should a late application be refused here, or sent to a caseworker?',
    });
    await page.clock.fastForward(6_000);

    await expect(page.locator('[data-wayfinder-stage="application-form"]')).toHaveAttribute('data-wayfinder-stage-notes', '1');
    await page.getByRole('tab', { name: /Notes/ }).click();
    const note = page.locator('[data-wayfinder-design-note]');
    await expect(note).toContainText('Design partner');

    await note.getByRole('button', { name: 'Show on canvas' }).click();

    await expect(page.locator('[data-wayfinder-stage="application-form"]')).toHaveAttribute('aria-pressed', 'true');
  });

  test('when the design partner resolves a note, it moves to Resolved with what was done, and can be reopened', async ({ page }) => {
    await openEditor(page);
    await addNoteTo(page, 'declaration', 'Make the declaration plainer.');

    await asDesignPartner(page, 'resolve', { text: 'Rewrote the declaration in plain English.' });
    await page.clock.fastForward(6_000);

    await expect(page.locator('[data-wayfinder-stage="declaration"]')).not.toHaveAttribute('data-wayfinder-stage-notes', /.*/);
    await page.getByRole('tab', { name: /Notes/ }).click();
    await expect(page.locator('[data-wayfinder-design-note]')).toHaveCount(0);
    await page.getByRole('button', { name: 'Resolved (1)' }).click();
    await expect(page.locator('[data-wayfinder-design-note-resolution]')).toContainText(
      'Resolved by Design partner in version 1: Rewrote the declaration in plain English.'
    );

    await page.getByRole('button', { name: 'Reopen' }).click();

    await page.getByRole('button', { name: 'Open (1)' }).click();
    await expect(page.locator('[data-wayfinder-design-note]')).toContainText('Rewrote the declaration in plain English.');
  });

  test('replying keeps the note open, and resolving it from the editor records what was done', async ({ page }) => {
    await openEditor(page);
    await addNoteTo(page, 'declaration', 'Too long.');
    await page.getByRole('tab', { name: /Notes/ }).click();
    const note = page.locator('[data-wayfinder-design-note]');

    await note.getByRole('textbox').fill('Cut it to two sentences?');
    await note.getByRole('button', { name: 'Reply' }).click();
    await expect(note.getByRole('list', { name: 'Replies' })).toContainText('You: Cut it to two sentences?');

    await note.getByRole('textbox').fill('Shortened it.');
    await note.getByRole('button', { name: 'Resolve' }).click();

    await expect(page.getByRole('button', { name: 'Resolved (1)' })).toBeVisible();
    await expect(page.locator('[data-wayfinder-tab-badge="notes"]')).toHaveCount(0);
  });

  test('screenshot: notes on the canvas and in the Notes tab', async ({ page }) => {
    await openEditor(page);
    await addNoteTo(page, 'declaration', 'Is a declaration needed for renewals?');
    await asDesignPartner(page, 'add', {
      anchor: { kind: 'Stage', key: 'application-form' },
      text: 'Should a late application be refused here, or sent to a caseworker?',
    });
    await page.clock.fastForward(6_000);
    await expect(page.locator('[data-wayfinder-stage="application-form"]')).toHaveAttribute('data-wayfinder-stage-notes', '1');

    await captureDocScreenshot(
      page.locator('wayfinder-service-blueprint-editor'),
      'docs/skills/canvas-editor/screenshots/design-notes-canvas.png'
    );
    await page.getByRole('tab', { name: /Notes/ }).click();
    await captureDocScreenshot(
      page.locator('wayfinder-service-blueprint-editor'),
      'docs/skills/canvas-editor/screenshots/design-notes-tab.png'
    );
  });
});
