/**
 * Bounded undo/redo for an editing session. It stores whole snapshots (the caller decides what a snapshot is), so it
 * knows nothing about blueprints, selections or rendering.
 */
export class EditHistory<TSnapshot> {
  private undoStack: TSnapshot[] = [];
  private redoStack: TSnapshot[] = [];

  constructor(private readonly limit = 50) {}

  get canUndo(): boolean {
    return this.undoStack.length > 0;
  }

  get canRedo(): boolean {
    return this.redoStack.length > 0;
  }

  /** A new edit is about to replace `before`: remember it, and discard anything that could have been redone. */
  record(before: TSnapshot): void {
    this.undoStack = [...this.undoStack, before].slice(-this.limit);
    this.redoStack = [];
  }

  /** Step back. `current` is the state being left; returns the state to restore, or undefined if there is nothing to undo. */
  undo(current: TSnapshot): TSnapshot | undefined {
    const previous = this.undoStack.at(-1);
    if (previous === undefined) {
      return undefined;
    }
    this.undoStack = this.undoStack.slice(0, -1);
    this.redoStack = [...this.redoStack, current].slice(-this.limit);
    return previous;
  }

  /** Step forward again. `current` is the state being left; returns the state to restore, or undefined if nothing was undone. */
  redo(current: TSnapshot): TSnapshot | undefined {
    const next = this.redoStack.at(-1);
    if (next === undefined) {
      return undefined;
    }
    this.redoStack = this.redoStack.slice(0, -1);
    this.undoStack = [...this.undoStack, current].slice(-this.limit);
    return next;
  }

  clear(): void {
    this.undoStack = [];
    this.redoStack = [];
  }

  /** One sentence for assistive technology: how much can be undone and redone. */
  get summary(): string {
    if (!this.canUndo && !this.canRedo) {
      return 'No editor changes yet. Undo and redo will appear as you edit.';
    }

    const plural = (count: number) => `${count} change${count === 1 ? '' : 's'}`;
    const undoLabel = `${plural(this.undoStack.length)} available to undo`;
    const redoLabel = this.canRedo ? `${plural(this.redoStack.length)} available to redo` : 'Redo disabled — you are at the latest change';
    return `${undoLabel}. ${redoLabel}.`;
  }
}
