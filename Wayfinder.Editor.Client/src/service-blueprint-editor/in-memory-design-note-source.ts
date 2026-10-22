import type { DesignNoteSource } from './design-note-source.js';
import type { DesignNote, DesignNoteAnchor, DesignNoteAuthor } from './types.js';

/**
 * Page-lifetime design notes, for stories, tests and a host with no backend. `currentVersion` names
 * the blueprint version a resolution is recorded against, as a real host's store does.
 */
export class InMemoryDesignNoteSource implements DesignNoteSource {
  private notes: DesignNote[] = [];
  private nextId = 1;

  constructor(
    private readonly currentVersion: (blueprintKey: string) => number,
    private readonly author: DesignNoteAuthor = { name: 'You', kind: 'Person' }
  ) {}

  async list(blueprintKey: string): Promise<DesignNote[]> {
    return structuredClone(this.notes.filter((note) => note.blueprintKey === blueprintKey));
  }

  async add(blueprintKey: string, anchor: DesignNoteAnchor, text: string, author: DesignNoteAuthor = this.author): Promise<DesignNote> {
    const note: DesignNote = {
      id: `note-${this.nextId++}`,
      blueprintKey,
      anchor,
      text: text.trim(),
      author,
      status: 'Open',
      replies: [],
      createdAt: new Date().toISOString(),
      revision: 1,
    };
    this.notes.push(note);
    return structuredClone(note);
  }

  reply(blueprintKey: string, noteId: string, text: string, author: DesignNoteAuthor = this.author): Promise<DesignNote> {
    return this.change(blueprintKey, noteId, (note) => {
      note.replies.push({ author, text: text.trim(), createdAt: new Date().toISOString() });
    });
  }

  resolve(blueprintKey: string, noteId: string, summary: string, author: DesignNoteAuthor = this.author): Promise<DesignNote> {
    return this.change(blueprintKey, noteId, (note) => {
      note.status = 'Resolved';
      note.resolution = {
        author,
        summary: summary.trim(),
        version: this.currentVersion(blueprintKey),
        resolvedAt: new Date().toISOString(),
      };
    });
  }

  reopen(blueprintKey: string, noteId: string, reason?: string): Promise<DesignNote> {
    return this.change(blueprintKey, noteId, (note) => {
      if (note.resolution) {
        const { author, version, summary, resolvedAt } = note.resolution;
        note.replies.push({ author, text: `Resolved in version ${version}: ${summary}`, createdAt: resolvedAt });
      }
      if (reason?.trim()) {
        note.replies.push({ author: this.author, text: reason.trim(), createdAt: new Date().toISOString() });
      }
      note.status = 'Open';
      note.resolution = undefined;
    });
  }

  async remove(blueprintKey: string, noteId: string): Promise<void> {
    this.notes = this.notes.filter((note) => note.blueprintKey !== blueprintKey || note.id !== noteId);
  }

  private async change(blueprintKey: string, noteId: string, apply: (note: DesignNote) => void): Promise<DesignNote> {
    const note = this.notes.find((candidate) => candidate.blueprintKey === blueprintKey && candidate.id === noteId);
    if (!note) {
      throw new Error(`There is no note '${noteId}'.`);
    }
    apply(note);
    note.revision += 1;
    return structuredClone(note);
  }
}
