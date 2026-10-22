import type { DesignNote, DesignNoteAnchor } from './types.js';

/**
 * Design notes: feedback pinned to part of a blueprint, by the designer or by an AI design partner
 * working through the MCP tools. A host offers them by giving its `ServiceBlueprintSource` a
 * `designNotes`; without one the editor shows no notes at all. The host decides who may read and
 * write them, and who a new note is from.
 */
export interface DesignNoteSource {
  list(blueprintKey: string): Promise<DesignNote[]>;
  add(blueprintKey: string, anchor: DesignNoteAnchor, text: string): Promise<DesignNote>;
  reply(blueprintKey: string, noteId: string, text: string): Promise<DesignNote>;
  resolve(blueprintKey: string, noteId: string, summary: string): Promise<DesignNote>;
  reopen(blueprintKey: string, noteId: string, reason?: string): Promise<DesignNote>;
  remove(blueprintKey: string, noteId: string): Promise<void>;
}

export interface HttpDesignNoteSourceOptions {
  /** The URL a blueprint key is appended to, e.g. `/wayfinder/service-blueprint-authoring/blueprints`. */
  blueprintsUrl: string;
  /** Extra headers for every request, such as a bearer token. Called afresh each time, so a rotated token is picked up. */
  headers?: () => Promise<Record<string, string>> | Record<string, string>;
}

/**
 * Talks to the design-note routes `MapServiceBlueprintAuthoringApi` maps (`{blueprint}/notes`,
 * `{blueprint}/notes/{id}/replies`, `/resolve`, `/reopen`). A host serving the same routes under
 * another base URL, such as a CMS management API, reuses it with its own `blueprintsUrl`.
 */
export class HttpDesignNoteSource implements DesignNoteSource {
  constructor(private readonly options: HttpDesignNoteSourceOptions) {}

  list(blueprintKey: string): Promise<DesignNote[]> {
    return this.send<DesignNote[]>('GET', this.notesUrl(blueprintKey));
  }

  add(blueprintKey: string, anchor: DesignNoteAnchor, text: string): Promise<DesignNote> {
    return this.send('POST', this.notesUrl(blueprintKey), { anchor, text });
  }

  reply(blueprintKey: string, noteId: string, text: string): Promise<DesignNote> {
    return this.send('POST', this.noteUrl(blueprintKey, noteId, '/replies'), { text });
  }

  resolve(blueprintKey: string, noteId: string, summary: string): Promise<DesignNote> {
    return this.send('POST', this.noteUrl(blueprintKey, noteId, '/resolve'), { summary });
  }

  reopen(blueprintKey: string, noteId: string, reason?: string): Promise<DesignNote> {
    return this.send('POST', this.noteUrl(blueprintKey, noteId, '/reopen'), { reason: reason ?? null });
  }

  async remove(blueprintKey: string, noteId: string): Promise<void> {
    await this.send('DELETE', this.noteUrl(blueprintKey, noteId, ''));
  }

  private notesUrl(blueprintKey: string): string {
    return `${this.options.blueprintsUrl.replace(/\/$/, '')}/${encodeURIComponent(blueprintKey)}/notes`;
  }

  private noteUrl(blueprintKey: string, noteId: string, action: string): string {
    return `${this.notesUrl(blueprintKey)}/${encodeURIComponent(noteId)}${action}`;
  }

  private async send<T>(method: string, url: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json', ...(await this.options.headers?.()) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    const response = await fetch(url, {
      method,
      headers,
      credentials: 'same-origin',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      const payload = (await response.json().catch(() => null)) as { error?: unknown } | null;
      throw new Error(typeof payload?.error === 'string' ? payload.error : `The note could not be saved (${response.status}).`);
    }
    return (response.status === 204 ? undefined : await response.json()) as T;
  }
}
