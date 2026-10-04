import { nothing } from 'lit';
import { html, unsafeStatic } from 'lit/static-html.js';
import { type ToolbarIconName, renderToolbarIcon } from './graph/toolbar-icons.js';
import type { findServiceBlueprintShortcut } from './editor-shortcuts.js';
import { COPY_SHORTCUT, HELP_SHORTCUT, PASTE_SHORTCUT, REDO_SHORTCUT, SAVE_SHORTCUT, UNDO_SHORTCUT } from './editor-shortcut-bindings.js';

type Shortcut = ReturnType<typeof findServiceBlueprintShortcut>;

/** What the toolbar shows and does. Everything is a plain value or callback: the toolbar owns no state. */
export interface ToolbarModel {
  title: string;
  save: { saving: boolean; enabled: boolean; allowedByContext: boolean; dirtySummary: string; onSave(): void };
  history: { canUndo: boolean; canRedo: boolean; onUndo(): void; onRedo(): void };
  /** Only present while the Canvas tab is showing: copy/paste, add and zoom make no sense without the graph. */
  canvas: CanvasToolsModel | null;
}

export interface CanvasToolsModel {
  clipboard: { canCopy: boolean; canPaste: boolean; pasteTitle: string; onCopy(): void; onPaste(): void };
  onHelp(trigger: HTMLElement): void;
  graph: {
    addStage(trigger: HTMLElement): void;
    addGateway(trigger: HTMLElement): void;
    tidyLayout(): void;
    zoomOut(): void;
    zoomIn(): void;
    fitToScreen(): void;
    fitWidth(): void;
    zoomPercent: number;
  };
}

interface IconButton {
  icon: ToolbarIconName;
  label: string;
  onClick(event: Event): void;
  /** Test hook, e.g. `wayfinder-save` renders as the `data-wayfinder-save` attribute. */
  hook?: string;
  title?: string;
  disabled?: boolean;
  primary?: boolean;
  spinning?: boolean;
  shortcut?: Shortcut;
}

const withShortcut = (title: string, shortcut: Shortcut) => `${title}${shortcut ? ` (${shortcut.labels[0]})` : ''}`;

function iconButton(button: IconButton) {
  const hook = unsafeStatic(button.hook ? `data-${button.hook}` : '');
  const kind = button.primary ? 'govuk-button' : 'govuk-button govuk-button--secondary';
  return html`
    <button
      class="toolbar-btn toolbar-btn--icon ${unsafeStatic(kind)}${button.spinning ? ' toolbar-btn--spinning' : ''}"
      ${hook}
      ?disabled=${button.disabled}
      aria-label=${button.label}
      title=${button.title ?? button.label}
      aria-keyshortcuts=${button.shortcut?.ariaKeys ?? nothing}
      @click=${button.onClick}
    >
      ${renderToolbarIcon(button.icon)}
    </button>
  `;
}

const divider = html`<span class="toolbar-divider" role="separator" aria-orientation="vertical"></span>`;

function renderSave({ save }: ToolbarModel) {
  const title = save.allowedByContext
    ? `${save.dirtySummary} — ${save.saving ? 'Saving…' : 'Save'}${SAVE_SHORTCUT ? ` (${SAVE_SHORTCUT.labels[0]})` : ''}`
    : 'Saving is disabled for the current author.';
  return iconButton({
    icon: save.saving ? 'saving' : 'save',
    label: save.saving ? 'Saving' : 'Save',
    hook: 'wayfinder-save',
    title,
    disabled: !save.enabled,
    primary: true,
    spinning: save.saving,
    shortcut: SAVE_SHORTCUT,
    onClick: () => save.onSave(),
  });
}

function renderHistory({ history }: ToolbarModel) {
  return html`
    ${iconButton({ icon: 'undo', label: 'Undo', hook: 'wayfinder-undo', title: withShortcut('Undo', UNDO_SHORTCUT), disabled: !history.canUndo, shortcut: UNDO_SHORTCUT, onClick: () => history.onUndo() })}
    ${iconButton({ icon: 'redo', label: 'Redo', hook: 'wayfinder-redo', title: withShortcut('Redo', REDO_SHORTCUT), disabled: !history.canRedo, shortcut: REDO_SHORTCUT, onClick: () => history.onRedo() })}
  `;
}

function renderClipboardAndHelp({ clipboard, onHelp }: CanvasToolsModel) {
  return html`
    ${iconButton({ icon: 'copy', label: 'Copy', hook: 'wayfinder-copy', title: withShortcut('Copy', COPY_SHORTCUT), disabled: !clipboard.canCopy, shortcut: COPY_SHORTCUT, onClick: () => clipboard.onCopy() })}
    ${iconButton({ icon: 'paste', label: 'Paste', hook: 'wayfinder-paste', title: withShortcut(clipboard.pasteTitle, PASTE_SHORTCUT), disabled: !clipboard.canPaste, shortcut: PASTE_SHORTCUT, onClick: () => clipboard.onPaste() })}
    ${iconButton({ icon: 'help', label: 'Help', hook: 'wayfinder-help', title: withShortcut('Help', HELP_SHORTCUT), shortcut: HELP_SHORTCUT, onClick: (event) => onHelp(event.currentTarget as HTMLElement) })}
  `;
}

function renderAdding({ graph }: CanvasToolsModel) {
  return html`
    ${iconButton({ icon: 'addStage', label: 'Add stage', hook: 'wayfinder-add-stage', onClick: (event) => graph.addStage(event.currentTarget as HTMLElement) })}
    ${iconButton({ icon: 'addGateway', label: 'Add gateway', hook: 'wayfinder-add-gateway', onClick: (event) => graph.addGateway(event.currentTarget as HTMLElement) })}
    ${iconButton({ icon: 'tidyLayout', label: 'Tidy layout', hook: 'wayfinder-auto-arrange', onClick: () => graph.tidyLayout() })}
  `;
}

function renderZoom({ graph }: CanvasToolsModel) {
  return html`
    ${iconButton({ icon: 'zoomOut', label: 'Zoom out', onClick: () => graph.zoomOut() })}
    <span class="zoom-indicator" data-wayfinder-zoom>${graph.zoomPercent}%</span>
    ${iconButton({ icon: 'zoomIn', label: 'Zoom in', onClick: () => graph.zoomIn() })}
    ${iconButton({ icon: 'fitToScreen', label: 'Fit to screen', hook: 'wayfinder-fit-screen', onClick: () => graph.fitToScreen() })}
    ${iconButton({ icon: 'fitWidth', label: 'Fit width', hook: 'wayfinder-fit-width', onClick: () => graph.fitWidth() })}
  `;
}

/**
 * The bar above the whole tabbed area. Save, undo and redo act on the whole blueprint, so they stay
 * usable on every tab; the rest only makes sense with the graph on screen.
 */
export function renderEditorToolbar(model: ToolbarModel) {
  const { canvas } = model;
  return html`
    <div class="toolbar-header" role="none">
      <h1 id="service-blueprint-editor-title" class="editor-title">${model.title}</h1>
      <div class="toolbar-actions" role="toolbar" aria-label="ServiceBlueprint editor tools">
        ${renderSave(model)}
        ${renderHistory(model)}
        ${
          canvas
            ? html`
              ${divider}
              <div class="editor-toolbar">
                ${renderClipboardAndHelp(canvas)}
                ${divider}
                ${renderAdding(canvas)}
                ${divider}
                ${renderZoom(canvas)}
              </div>
            `
            : nothing
        }
      </div>
    </div>
  `;
}
