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

    const binding = this.bindings().find(({ shortcut }) => shortcut && matchesShortcut(event, shortcut));
    if (!binding || (binding.skipWhileTyping && this.isTypingInAField(event))) {
      return;
    }
    binding.run(event);
  };

  /** Checked in order; copy and paste leave the browser's own behaviour alone while an author is typing in a field. */
  private bindings() {
    const { commands } = this;
    return [
      {
        shortcut: SAVE_SHORTCUT,
        run: (event: KeyboardEvent) => {
          event.preventDefault();
          commands.save();
        },
      },
      {
        shortcut: COPY_SHORTCUT,
        skipWhileTyping: true,
        run: (event: KeyboardEvent) => {
          if (commands.copy()) event.preventDefault();
        },
      },
      {
        shortcut: PASTE_SHORTCUT,
        skipWhileTyping: true,
        run: (event: KeyboardEvent) => {
          if (commands.paste()) event.preventDefault();
        },
      },
      {
        shortcut: REDO_SHORTCUT,
        run: (event: KeyboardEvent) => {
          event.preventDefault();
          if (commands.canRedo()) commands.redo();
        },
      },
      {
        shortcut: UNDO_SHORTCUT,
        run: (event: KeyboardEvent) => {
          event.preventDefault();
          if (commands.canUndo()) commands.undo();
        },
      },
    ];
  }

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
