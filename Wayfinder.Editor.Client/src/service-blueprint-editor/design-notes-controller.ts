import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { type AnchorTarget, anchorForSelection, placeAnchor, targetForAnchor } from './design-note-anchors.js';
import type { DesignNoteSource } from './design-note-source.js';
import type { EditorSelection } from './editor-selection.js';
import type { DesignNote, DesignNoteAnchor, ServiceBlueprint } from './types.js';

/** Notes change without the blueprint changing (an agent resolving one, say), so they are checked on their own. */
export const NOTES_POLL_INTERVAL_MS = 5_000;

/** What design notes need from the editor, read afresh each time. */
export interface DesignNotesInputs {
  source: () => DesignNoteSource | undefined;
  blueprintKey: () => string;
  blueprint: () => ServiceBlueprint | null;
  selection: () => EditorSelection;
  transitionIndex: () => number | null;
  /** Show an anchor on the canvas. */
  show: (target: AnchorTarget) => void;
  toast: (message: string) => void;
}

export type NotesFilter = 'Open' | 'Resolved';

/**
 * The design notes on the open blueprint: loading and refreshing them, the open-note counts the
 * canvas marks nodes with, and adding, answering and resolving them through the host's
 * `DesignNoteSource`. Does nothing when the host has no notes.
 */
export class DesignNotesController implements ReactiveController {
  notes: DesignNote[] = [];
  filter: NotesFilter = 'Open';
  busy = false;
  private counted: { notes: DesignNote[]; blueprint: ServiceBlueprint | null; counts: Record<string, number> } = {
    notes: [],
    blueprint: null,
    counts: {},
  };
  private loadedFor = '';
  private timer: number | null = null;

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: DesignNotesInputs
  ) {
    host.addController(this);
  }

  get available(): boolean {
    return this.inputs.source() !== undefined;
  }

  get openCount(): number {
    return this.notes.filter((note) => note.status === 'Open').length;
  }

  /** Open notes per canvas node (`stage:key`, `gateway:key`); the same object until a count changes, so the canvas only redraws on a real change. */
  get openCounts(): Record<string, number> {
    const blueprint = this.inputs.blueprint();
    if (this.counted.notes !== this.notes || this.counted.blueprint !== blueprint) {
      const counts = countOpen(this.notes, blueprint);
      this.counted = {
        notes: this.notes,
        blueprint,
        counts: JSON.stringify(counts) === JSON.stringify(this.counted.counts) ? this.counted.counts : counts,
      };
    }
    return this.counted.counts;
  }

  get visible(): DesignNote[] {
    return this.notes.filter((note) => note.status === this.filter);
  }

  hostConnected(): void {
    this.schedule();
  }

  hostDisconnected(): void {
    this.stop();
  }

  setFilter(filter: NotesFilter): void {
    this.filter = filter;
    this.host.requestUpdate();
  }

  /** What a note made now would be pinned to. */
  newNoteAnchor(): DesignNoteAnchor {
    const blueprint = this.inputs.blueprint();
    return blueprint ? anchorForSelection(blueprint, this.inputs.selection(), this.inputs.transitionIndex()) : { kind: 'Blueprint' };
  }

  label(anchor: DesignNoteAnchor): { text: string; removed: boolean; target: AnchorTarget | null } {
    const blueprint = this.inputs.blueprint();
    if (!blueprint) {
      return { text: '', removed: false, target: null };
    }
    const place = placeAnchor(blueprint, anchor);
    return { text: place.label, removed: place.removed, target: targetForAnchor(blueprint, anchor) };
  }

  show(note: DesignNote): void {
    const target = this.label(note.anchor).target;
    if (target) {
      this.inputs.show(target);
    }
  }

  async refresh(): Promise<void> {
    const source = this.inputs.source();
    const key = this.inputs.blueprintKey();
    if (!source || !key) {
      this.adopt([], key);
      return;
    }
    try {
      const notes = await source.list(key);
      if (key === this.inputs.blueprintKey()) {
        this.adopt(notes, key);
      }
    } catch {
      // Notes are an aid, never a reason the editor stops working: keep what is shown and try again on the next tick.
    }
  }

  add(text: string): Promise<boolean> {
    return this.act((source, key) => source.add(key, this.newNoteAnchor(), text), 'Note added.');
  }

  reply(note: DesignNote, text: string): Promise<boolean> {
    return this.act((source, key) => source.reply(key, note.id, text), 'Reply added.');
  }

  resolve(note: DesignNote, summary: string): Promise<boolean> {
    return this.act((source, key) => source.resolve(key, note.id, summary), 'Note resolved.');
  }

  reopen(note: DesignNote): Promise<boolean> {
    return this.act((source, key) => source.reopen(key, note.id), 'Note reopened.');
  }

  remove(note: DesignNote): Promise<boolean> {
    return this.act((source, key) => source.remove(key, note.id), 'Note deleted.');
  }

  private async act(change: (source: DesignNoteSource, key: string) => Promise<unknown>, done: string): Promise<boolean> {
    const source = this.inputs.source();
    if (!source || this.busy) {
      return false;
    }
    this.busy = true;
    this.host.requestUpdate();
    try {
      await change(source, this.inputs.blueprintKey());
      await this.refresh();
      this.inputs.toast(done);
      return true;
    } catch (error) {
      this.inputs.toast(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      this.busy = false;
      this.host.requestUpdate();
    }
  }

  private adopt(notes: DesignNote[], key: string): void {
    if (key === this.loadedFor && JSON.stringify(notes) === JSON.stringify(this.notes)) {
      return;
    }
    this.loadedFor = key;
    this.notes = notes;
    this.host.requestUpdate();
  }

  private schedule(): void {
    this.stop();
    if (typeof window === 'undefined') {
      return;
    }
    this.timer = window.setTimeout(async () => {
      await this.refresh();
      this.schedule();
    }, NOTES_POLL_INTERVAL_MS);
  }

  private stop(): void {
    if (this.timer !== null) {
      window.clearTimeout(this.timer);
      this.timer = null;
    }
  }
}

function countOpen(notes: DesignNote[], blueprint: ServiceBlueprint | null): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const note of notes) {
    const nodeId = blueprint && note.status === 'Open' ? placeAnchor(blueprint, note.anchor).nodeId : null;
    if (nodeId) {
      counts[nodeId] = (counts[nodeId] ?? 0) + 1;
    }
  }
  return counts;
}
