import { type ReactiveController, type ReactiveControllerHost, type TemplateResult, html, nothing } from 'lit';
import type { ServiceBlueprintSource } from './service-blueprint-source.js';

/** What staleness detection needs from the editor, read afresh each time. */
export interface StalenessInputs {
  source: () => ServiceBlueprintSource | undefined;
  blueprintKey: () => string;
  /** The version of the blueprint the author is looking at, or null when nothing is loaded. */
  loadedVersion: () => number | null;
  /** Replace the view with the latest saved version. */
  reload: () => Promise<void>;
}

const POLL_INTERVAL_MS = 15_000;

/**
 * Knows whether someone else (a person, or an AI agent) has saved a newer version than the one being edited. It finds
 * out proactively by polling, or reactively when a save is refused with a conflict. Once stale the editor is
 * read-only until reload: any further edit would only head toward another guaranteed conflict, so there is no honest
 * "keep working" option. Hosts whose source cannot `checkVersion` simply get no polling.
 */
export class StalenessController implements ReactiveController {
  stale = false;
  currentVersion: number | null = null;
  private bannerDismissed = false;
  private timer: number | null = null;

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: StalenessInputs
  ) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.stopPolling();
  }

  /** A fresh blueprint was loaded: it is current by definition, so clear staleness and start polling again. */
  reset(): void {
    this.stale = false;
    this.currentVersion = null;
    this.bannerDismissed = false;
    this.schedule();
    this.host.requestUpdate();
  }

  markStale(currentVersion: number | null): void {
    this.stale = true;
    this.currentVersion = currentVersion;
    this.bannerDismissed = false;
    this.stopPolling();
    this.host.requestUpdate();
  }

  /** Poll again in 15s. A cheap scalar check; Server-Sent Events is the upgrade path if that stops being true. */
  schedule(): void {
    this.stopPolling();
    // No point polling once we already know it's stale: nothing more to learn until reload.
    if (typeof window === 'undefined' || !this.inputs.source()?.checkVersion || this.inputs.loadedVersion() === null || this.stale) {
      return;
    }
    this.timer = window.setTimeout(() => void this.poll(), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.timer !== null && typeof window !== 'undefined') {
      window.clearTimeout(this.timer);
    }
    this.timer = null;
  }

  private async poll(): Promise<void> {
    const source = this.inputs.source();
    const loadedVersion = this.inputs.loadedVersion();
    if (!source?.checkVersion || loadedVersion === null) {
      return;
    }

    const key = this.inputs.blueprintKey();
    try {
      const currentVersion = await source.checkVersion(key);
      // The author may have switched blueprint, or reloaded, while this was in flight.
      if (key !== this.inputs.blueprintKey() || this.inputs.loadedVersion() === null) {
        return;
      }
      if (currentVersion !== null && currentVersion !== loadedVersion && !this.stale) {
        this.markStale(currentVersion);
      }
    } catch {
      // Best-effort: a transient poll failure shouldn't disrupt editing.
    } finally {
      this.schedule();
    }
  }

  /**
   * Detailed, dismissible notice at the top of the editor. Dismissing only hides the detail; the read-only overlay (with
   * its own Reload) stays in effect. The only reason to dismiss without reloading is to look at your own in-progress
   * changes first.
   */
  renderBanner(): TemplateResult | typeof nothing {
    if (!this.stale || this.bannerDismissed) {
      return nothing;
    }

    return html`
      <section
        class="stale-service-blueprint-banner"
        aria-labelledby="service-blueprint-stale-title"
        tabindex="-1"
        data-wayfinder-stale-service-blueprint-banner
      >
        <div class="stale-service-blueprint-header">
          <p class="stale-service-blueprint-eyebrow">Changed elsewhere</p>
          <h2 id="service-blueprint-stale-title" class="stale-service-blueprint-title">This service blueprint was updated elsewhere</h2>
          <p class="stale-service-blueprint-summary" role="alert">
            Someone else — a person in the editor, or an AI agent — saved a newer version
            ${this.currentVersion != null ? html`(now at version ${this.currentVersion})` : ''}
            while you were editing. The editor is read-only until you reload; reloading
            replaces your current view with the latest version, so copy anything you want to
            keep first.
          </p>
        </div>
        <div class="stale-service-blueprint-actions">
          <button
            type="button"
            class="toolbar-btn govuk-button"
            data-wayfinder-reload-after-conflict
            @click=${() => void this.inputs.reload()}
          >
            Reload latest version
          </button>
          <button
            type="button"
            class="toolbar-btn govuk-button govuk-button--secondary"
            aria-label="Dismiss — I just want to look at my changes first"
            data-wayfinder-dismiss-stale-banner
            @click=${() => {
              this.bannerDismissed = true;
              this.host.requestUpdate();
            }}
          >
            Dismiss
          </button>
        </div>
      </section>
    `;
  }

  /**
   * Blocks interaction with the canvas and inspector while stale. A translucent scrim, not an opaque one, so the author
   * can still see their in-progress content before reloading over it. It always carries its own Reload action.
   */
  renderOverlay(): TemplateResult | typeof nothing {
    if (!this.stale) {
      return nothing;
    }

    return html`
      <div class="stale-service-blueprint-overlay" data-wayfinder-stale-service-blueprint-overlay>
        <div class="stale-service-blueprint-overlay-ribbon" role="status">
          <span>Read-only — this service blueprint changed elsewhere.</span>
          <button
            type="button"
            class="toolbar-btn govuk-button stale-service-blueprint-overlay-reload"
            data-wayfinder-reload-after-conflict-overlay
            @click=${() => void this.inputs.reload()}
          >
            Reload latest version
          </button>
        </div>
      </div>
    `;
  }
}
