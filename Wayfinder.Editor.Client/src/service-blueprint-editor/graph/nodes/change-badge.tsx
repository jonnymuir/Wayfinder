import type { NodeChange } from '../graph-callbacks.js';

const LABELS = { added: 'New', changed: 'Changed' } as const;

/** Read after the node's own name, so a screen reader hears what the badge shows. */
export function changeDescription(change: NodeChange): string {
  return change ? `, ${change} in the latest version` : '';
}

/** A visible word as well as the outline colour, so the change never relies on colour alone. */
export function ChangeBadge({ change }: { change: NodeChange }) {
  return change ? (
    <span className={`change-badge change-${change}`} aria-hidden="true">
      {LABELS[change]}
    </span>
  ) : null;
}

/** Read after the node's own name, alongside any change. */
export function notesDescription(count: number): string {
  return count > 0 ? `, ${count} open ${count === 1 ? 'note' : 'notes'}` : '';
}

/** A note icon with the open-note count, shown until every note on the node is resolved. */
export function NoteMarker({ count }: { count: number }) {
  return count > 0 ? (
    <span className="note-marker" aria-hidden="true">
      <svg viewBox="0 0 16 16" width="12" height="12" focusable="false" aria-hidden="true">
        <path d="M2 2h12v9H6l-3 3v-3H2z" fill="currentColor" />
      </svg>
      {count}
    </span>
  ) : null;
}
