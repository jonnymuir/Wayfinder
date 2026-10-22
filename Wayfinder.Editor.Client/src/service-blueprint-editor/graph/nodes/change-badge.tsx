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
