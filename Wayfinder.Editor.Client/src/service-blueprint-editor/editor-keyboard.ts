import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { COPY_SHORTCUT, HELP_SHORTCUT, PASTE_SHORTCUT, REDO_SHORTCUT, SAVE_SHORTCUT, UNDO_SHORTCUT } from './editor-shortcut-bindings.js';
import { matchesShortcut } from './editor-shortcuts.js';

/** What the editor's keyboard shortcuts can do. Each returns true if it acted, so the key press is consumed only then. */
export interface EditorCommands {
  save(): void;
  undo(): void;
  redo(): void;
  copy(): boolean;
  paste(): boolean;
  canUndo(): boolean;
  canRedo(): boolean;
  openHelp(): void;
  isHelpOpen(): boolean;
}

type KeyboardHost = ReactiveControllerHost & HTMLElement;

/**
 * Turns key presses anywhere in the editor into commands. Listens in the capture phase so the shortcuts work whichever
 * panel has focus, and leaves copy and paste alone while the author is typing in a field.
 */
export class EditorKeyboard implements ReactiveController {
  constructor(
    private readonly host: KeyboardHost,
    private readonly commands: EditorCommands
  ) {
    host.addController(this);
  }

  hostConnected(): void {
    this.host.addEventListener('keydown', this.onKeydown, true);
  }

  hostDisconnected(): void {
    this.host.removeEventListener('keydown', this.onKeydown, true);
  }

  private onKeydown = (event: KeyboardEvent) => {
    if (!event.defaultPrevented && HELP_SHORTCUT && matchesShortcut(event, HELP_SHORTCUT)) {
      event.preventDefault();
      this.commands.openHelp();
      return;
    }

    if (this.commands.isHelpOpen() || event.defaultPrevented || event.altKey) {
      return;
    }

    if (SAVE_SHORTCUT && matchesShortcut(event, SAVE_SHORTCUT)) {
      event.preventDefault();
      this.commands.save();
      return;
    }

    const isCopy = COPY_SHORTCUT && matchesShortcut(event, COPY_SHORTCUT);
    const isPaste = PASTE_SHORTCUT && matchesShortcut(event, PASTE_SHORTCUT);
    if ((isCopy || isPaste) && this.isTypingInAField(event)) {
      return;
    }

    if (isCopy) {
      if (this.commands.copy()) {
        event.preventDefault();
      }
      return;
    }

    if (isPaste) {
      if (this.commands.paste()) {
        event.preventDefault();
      }
      return;
    }

    if (REDO_SHORTCUT && matchesShortcut(event, REDO_SHORTCUT)) {
      event.preventDefault();
      if (this.commands.canRedo()) {
        this.commands.redo();
      }
      return;
    }

    if (UNDO_SHORTCUT && matchesShortcut(event, UNDO_SHORTCUT)) {
      event.preventDefault();
      if (this.commands.canUndo()) {
        this.commands.undo();
      }
    }
  };

  private isTypingInAField(event: KeyboardEvent): boolean {
    return event
      .composedPath()
      .some(
        (target) =>
          target instanceof HTMLElement &&
          (target instanceof HTMLInputElement ||
            target instanceof HTMLTextAreaElement ||
            target instanceof HTMLSelectElement ||
            target.isContentEditable)
      );
  }
}
