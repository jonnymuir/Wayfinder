import { html, nothing, type TemplateResult } from 'lit';
import { anchorMatchesSelection } from './design-note-anchors.js';
import type { DesignNotesController } from './design-notes-controller.js';
import type { EditorSelection } from './editor-selection.js';
import type { DesignNote, DesignNoteAuthor, ServiceBlueprint } from './types.js';

const MAX_TEXT_LENGTH = 4000;

function authorName(author: DesignNoteAuthor): string {
  return author.kind === 'DesignPartner' ? 'Design partner' : author.name;
}

function textFrom(event: SubmitEvent): { form: HTMLFormElement; text: string; action: string } {
  event.preventDefault();
  const form = event.currentTarget as HTMLFormElement;
  const text = String(new FormData(form).get('text') ?? '').trim();
  const action = event.submitter instanceof HTMLButtonElement ? event.submitter.value : '';
  return { form, text, action };
}

async function submitNoteAction(notes: DesignNotesController, note: DesignNote, event: SubmitEvent) {
  const { form, text, action } = textFrom(event);
  if (!text) {
    return;
  }
  const done = action === 'resolve' ? await notes.resolve(note, text) : await notes.reply(note, text);
  if (done) {
    form.reset();
  }
}

function renderReplies(note: DesignNote): TemplateResult | typeof nothing {
  if (note.replies.length === 0) {
    return nothing;
  }
  return html`
    <ol class="design-note-replies" aria-label="Replies">
      ${note.replies.map((reply) => html`<li><span class="design-note-author">${authorName(reply.author)}</span>: ${reply.text}</li>`)}
    </ol>
  `;
}

function renderNoteActions(notes: DesignNotesController, note: DesignNote): TemplateResult {
  if (note.status === 'Resolved') {
    return html`
      <div class="design-note-actions">
        <button type="button" class="note-btn" ?disabled=${notes.busy} @click=${() => notes.reopen(note)}>Reopen</button>
        <button type="button" class="note-btn note-btn-warning" ?disabled=${notes.busy} @click=${() => notes.remove(note)}>Delete</button>
      </div>
    `;
  }
  const inputId = `design-note-answer-${note.id}`;
  return html`
    <form class="design-note-answer" @submit=${(event: SubmitEvent) => submitNoteAction(notes, note, event)}>
      <label class="note-label" for=${inputId}>Reply, or say what was done to resolve it</label>
      <textarea id=${inputId} class="note-textarea" name="text" rows="2" maxlength=${MAX_TEXT_LENGTH} required></textarea>
      <div class="design-note-actions">
        <button type="submit" class="note-btn" value="reply" ?disabled=${notes.busy}>Reply</button>
        <button type="submit" class="note-btn note-btn-primary" value="resolve" ?disabled=${notes.busy}>Resolve</button>
        <button type="button" class="note-btn note-btn-warning" ?disabled=${notes.busy} @click=${() => notes.remove(note)}>Delete</button>
      </div>
    </form>
  `;
}

function renderNote(notes: DesignNotesController, note: DesignNote): TemplateResult {
  const place = notes.label(note.anchor);
  return html`
    <li>
      <article class="design-note" aria-label=${`Note on ${place.text}`} data-wayfinder-design-note=${note.id} data-wayfinder-design-note-status=${note.status}>
        <p class=${`design-note-anchor ${place.removed ? 'design-note-anchor-removed' : ''}`}>
          ${place.text}
          ${place.target ? html`<button type="button" class="design-note-show" @click=${() => notes.show(note)}>Show on canvas</button>` : nothing}
        </p>
        <p class="design-note-text">${note.text}</p>
        <p class="design-note-meta"><span class="design-note-author">${authorName(note.author)}</span></p>
        ${renderReplies(note)}
        ${
          note.resolution
            ? html`<p class="design-note-resolution" data-wayfinder-design-note-resolution>
                Resolved by ${authorName(note.resolution.author)} in version ${note.resolution.version}: ${note.resolution.summary}
              </p>`
            : nothing
        }
        ${renderNoteActions(notes, note)}
      </article>
    </li>
  `;
}

/** The Notes tab: every note on the blueprint, open or resolved. */
export function renderNotesPanel(notes: DesignNotesController): TemplateResult {
  const resolved = notes.notes.length - notes.openCount;
  const filterButton = (filter: 'Open' | 'Resolved', count: number) => html`
    <button type="button" class="design-notes-filter" aria-pressed=${notes.filter === filter} @click=${() => notes.setFilter(filter)}>
      ${filter} (${count})
    </button>
  `;
  return html`
    <section class="design-notes" aria-labelledby="design-notes-title" data-wayfinder-design-notes>
      <h2 id="design-notes-title" class="design-notes-title">Design notes</h2>
      <p class="design-notes-hint">
        Feedback pinned to this blueprint, by you or by an AI design partner. A partner connected to the authoring tools can
        work through open notes and resolve each one with what it changed.
      </p>
      <div class="design-notes-filters" role="group" aria-label="Show notes">
        ${filterButton('Open', notes.openCount)} ${filterButton('Resolved', resolved)}
      </div>
      ${
        notes.visible.length === 0
          ? html`<p class="design-notes-empty">${notes.filter === 'Open' ? 'No open notes.' : 'No resolved notes.'}</p>`
          : html`<ol class="design-note-list">${notes.visible.map((note) => renderNote(notes, note))}</ol>`
      }
    </section>
  `;
}

async function submitNewNote(notes: DesignNotesController, event: SubmitEvent) {
  const { form, text } = textFrom(event);
  if (text && (await notes.add(text))) {
    form.reset();
  }
}

/** Under the Properties panel: the open notes on what is selected, and a way to add one. */
export function renderSelectionNotes(
  notes: DesignNotesController,
  blueprint: ServiceBlueprint | null,
  selection: EditorSelection,
  openNotesTab: () => void
): TemplateResult | typeof nothing {
  if (!notes.available || !blueprint) {
    return nothing;
  }
  const here = notes.notes.filter((note) => note.status === 'Open' && anchorMatchesSelection(blueprint, note.anchor, selection));
  const anchor = notes.label(notes.newNoteAnchor()).text;
  return html`
    <section class="selection-notes" aria-labelledby="selection-notes-title" data-wayfinder-selection-notes>
      <h3 id="selection-notes-title" class="selection-notes-title">Notes</h3>
      ${
        here.length > 0
          ? html`<ul class="selection-note-list">
              ${here.map((note) => html`<li><span class="design-note-author">${authorName(note.author)}</span>: ${note.text}</li>`)}
            </ul>
            <button type="button" class="design-note-show" @click=${openNotesTab}>Reply or resolve in Notes</button>`
          : nothing
      }
      <form class="design-note-add" data-wayfinder-add-design-note @submit=${(event: SubmitEvent) => submitNewNote(notes, event)}>
        <label class="note-label" for="design-note-new">Add a note to ${anchor}</label>
        <textarea id="design-note-new" class="note-textarea" name="text" rows="2" maxlength=${MAX_TEXT_LENGTH} required></textarea>
        <button type="submit" class="note-btn" ?disabled=${notes.busy}>Add note</button>
      </form>
    </section>
  `;
}
