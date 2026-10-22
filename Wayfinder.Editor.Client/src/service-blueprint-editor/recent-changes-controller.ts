import { type ReactiveControllerHost, type TemplateResult, html, nothing } from 'lit';
import { type BlueprintChanges, diffBlueprints, summariseChanges } from './blueprint-changes.js';
import type { GraphRecentChanges } from './graph/graph-callbacks.js';
import type { ServiceBlueprintSource } from './service-blueprint-source.js';
import type { ServiceBlueprint } from './types.js';

/** What following someone else's changes needs from the editor, read afresh each time. */
export interface RecentChangesInputs {
  source: () => ServiceBlueprintSource | undefined;
  blueprintKey: () => string;
  /** The blueprint on screen. */
  current: () => ServiceBlueprint | null;
  /** Show a newer version in place, keeping the author's selection where it still exists. */
  adopt: (latest: ServiceBlueprint) => void;
}

/**
 * Moves the canvas to a version someone else saved, typically an AI design partner, and shows what changed. Changes add
 * up from the version the author last acknowledged, so several saves in a row read as one summary and one set of
 * highlights, until the author dismisses them. Only used when the author has no unsaved work: otherwise the staleness
 * controller's conflict handling applies.
 */
export class RecentChangesController {
  /** For the canvas; the same object until the changes do, so the canvas only redraws on a real change. */
  highlight: GraphRecentChanges | null = null;
  private since: ServiceBlueprint | null = null;
  private changes: BlueprintChanges | null = null;
  private announcement = '';

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: RecentChangesInputs
  ) {}

  /** Load the latest version and move to it. Resolves once it is on screen, or straight away if there is nothing newer. */
  async follow(): Promise<void> {
    const source = this.inputs.source();
    const before = this.inputs.current();
    const key = this.inputs.blueprintKey();
    if (!source || !before) {
      return;
    }
    const latest = await source.load(key);
    if (!this.stillNewer(key, before, latest)) {
      return;
    }
    if (this.since?.definitionKey !== before.definitionKey) {
      this.since = before;
    }
    const changes = diffBlueprints(this.since, latest);
    this.inputs.adopt(latest);
    this.changes = changes;
    this.highlight = {
      addedNodeKeys: changes.added.map((node) => node.key),
      changedNodeKeys: changes.changed.map((node) => node.key),
      routeIndices: [...changes.addedRouteIndices, ...changes.changedRouteIndices],
    };
    this.announcement = `Updated to version ${latest.version}: ${summariseChanges(changes)}.`;
    this.host.requestUpdate();
  }

  /** The author may have switched blueprint while this was loading, or it may not be newer after all. */
  private stillNewer(key: string, before: ServiceBlueprint, latest: ServiceBlueprint): boolean {
    if (key !== this.inputs.blueprintKey() || this.inputs.current() !== before) {
      return false;
    }
    return latest.version > before.version;
  }

  /** The author has seen the changes. The next outside save starts a new summary. */
  dismiss(): void {
    this.since = null;
    this.changes = null;
    this.highlight = null;
    this.host.requestUpdate();
  }

  /** The live region is always present, so a screen reader announces each update as it arrives. */
  renderNotice(): TemplateResult {
    return html`
      <div class="sr-only" role="status" aria-live="polite" data-wayfinder-recent-changes-status>${this.announcement}</div>
      ${this.renderSummary()}
    `;
  }

  private renderSummary(): TemplateResult | typeof nothing {
    const current = this.inputs.current();
    if (!this.changes || !this.since || !current || current.definitionKey !== this.since.definitionKey) {
      return nothing;
    }
    const summary = summariseChanges(this.changes);
    const removed = this.changes.removed.map((node) => node.displayName);
    return html`
      <section class="recent-changes-notice" aria-labelledby="recent-changes-title" data-wayfinder-recent-changes>
        <div>
          <h2 id="recent-changes-title" class="recent-changes-title">
            Updated elsewhere, from version ${this.since.version} to ${current.version}
          </h2>
          <p class="recent-changes-summary">${summary.charAt(0).toUpperCase()}${summary.slice(1)}.</p>
          ${removed.length > 0 ? html`<p class="recent-changes-removed">Removed: ${removed.join(', ')}</p>` : nothing}
        </div>
        <button type="button" class="toolbar-btn govuk-button govuk-button--secondary" data-wayfinder-dismiss-recent-changes @click=${() => this.dismiss()}>
          Dismiss
        </button>
      </section>
    `;
  }
}
