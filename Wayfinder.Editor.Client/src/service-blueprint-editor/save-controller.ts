import { type ReactiveController, type ReactiveControllerHost, type TemplateResult, html, nothing } from 'lit';
import { cloneServiceBlueprint, serviceBlueprintsEqual } from './blueprint-snapshot.js';
import { ServiceBlueprintSaveError, type ServiceBlueprintSource, normaliseServiceBlueprintSaveError } from './service-blueprint-source.js';
import type { ServiceBlueprint } from './types.js';

type SaveState = 'idle' | 'saving' | 'saved' | 'error';

/** What saving needs from the editor, read afresh each time. */
export interface SaveInputs {
  blueprint: () => ServiceBlueprint | null;
  blueprintKey: () => string;
  source: () => ServiceBlueprintSource | undefined;
  hasBlockingIssues: () => boolean;
  /** False when the host says the current author may not save. */
  allowedByContext: () => boolean;
  /** The save succeeded and the version moved on: adopt this copy as the current blueprint. */
  adopt: (saved: ServiceBlueprint) => void;
  /** The store refused the save because someone else saved first. */
  conflict: (currentVersion: number | null) => void;
  toast: (message: string) => void;
  jumpToStage: (stageKey: string) => void;
}

type HostWithRoot = ReactiveControllerHost & { renderRoot: HTMLElement | DocumentFragment };

/**
 * Saving the blueprint and everything that follows from it: whether there is anything to save, whether saving is
 * allowed, the outcome of the last attempt, and the persistent error surface for a failed one.
 */
export class SaveController implements ReactiveController {
  state: SaveState = 'idle';
  private message: string | null = null;
  error: ServiceBlueprintSaveError | null = null;
  private copyStatus: string | null = null;
  /** The blueprint as last loaded or saved: "dirty" means different from this. */
  private saved: ServiceBlueprint | null = null;
  private focusErrorNext = false;

  constructor(
    private readonly host: HostWithRoot,
    private readonly inputs: SaveInputs
  ) {
    host.addController(this);
  }

  hostUpdated(): void {
    if (this.focusErrorNext && this.error) {
      this.focusErrorNext = false;
      this.host.renderRoot.querySelector<HTMLElement>('[data-wayfinder-save-error]')?.focus();
    }
  }

  get isSaving(): boolean {
    return this.state === 'saving';
  }

  get isDirty(): boolean {
    return !serviceBlueprintsEqual(this.inputs.blueprint(), this.saved);
  }

  get canSave(): boolean {
    return Boolean(this.inputs.blueprint()) && !this.inputs.hasBlockingIssues() && !this.isSaving && this.inputs.allowedByContext();
  }

  get dirtySummary(): string {
    if (!this.inputs.blueprint()) {
      return 'Service blueprint not loaded yet.';
    }
    return this.isDirty ? 'Unsaved changes' : 'All changes saved';
  }

  get statusSummary(): string {
    switch (this.state) {
      case 'saving':
        return 'Saving serviceBlueprint changes…';
      case 'saved':
        return this.message ?? 'Service blueprint changes saved.';
      case 'error':
        return this.message ?? 'Save failed.';
      default:
        return this.inputs.hasBlockingIssues()
          ? 'Save is blocked until the blocking validation errors are fixed.'
          : (this.message ?? 'Save is ready.');
    }
  }

  /** A blueprint was loaded: it is the new baseline and any earlier outcome is irrelevant. */
  loaded(blueprint: ServiceBlueprint): void {
    this.saved = cloneServiceBlueprint(blueprint);
    this.clearOutcome();
  }

  /** The author edited something, so "saved" / "failed" no longer describes the current content. */
  edited(): void {
    this.state = 'idle';
    this.message = null;
    this.host.requestUpdate();
  }

  clearOutcome(): void {
    this.state = 'idle';
    this.message = null;
    this.error = null;
    this.copyStatus = null;
    this.host.requestUpdate();
  }

  async save(): Promise<void> {
    const blueprint = this.inputs.blueprint();
    if (!blueprint) {
      return;
    }

    if (this.inputs.hasBlockingIssues()) {
      this.fail(
        new ServiceBlueprintSaveError({
          title: 'Can’t save this service blueprint yet',
          summary: 'Fix the blocking validation errors first.',
          detailLines: ['Open Validation to review each blocking error before trying again.'],
        })
      );
      return;
    }

    this.state = 'saving';
    this.message = null;
    this.copyStatus = null;
    this.host.requestUpdate();

    const source = this.inputs.source();
    if (!source) {
      this.fail(
        new ServiceBlueprintSaveError({
          title: 'Save unavailable',
          summary: 'No service blueprint source is wired to the editor.',
          detailLines: ['Connect a service blueprint source before trying to save.'],
        })
      );
      return;
    }

    try {
      await source.save(this.inputs.blueprintKey(), blueprint);
      // A save that did not conflict means the store incremented the version by exactly one, but source.save returns
      // nothing, so bump it locally: left stale, the next version poll would report the author's own save as someone
      // else's change.
      const next = cloneServiceBlueprint({ ...blueprint, version: blueprint.version + 1 });
      this.inputs.adopt(next);
      this.saved = cloneServiceBlueprint(next);
      this.state = 'saved';
      this.message = 'Service blueprint saved.';
      this.error = null;
      this.copyStatus = null;
      this.inputs.toast(this.message);
    } catch (error) {
      const normalised = normaliseServiceBlueprintSaveError(
        error,
        'The editor couldn’t save your changes. Review the details below and try again.'
      );

      if (normalised.isConflict) {
        // The same read-only treatment as a proactively detected staleness, however we found out.
        this.clearOutcome();
        this.inputs.conflict(normalised.currentVersion);
        return;
      }
      this.fail(normalised);
    }
    this.host.requestUpdate();
  }

  private fail(error: ServiceBlueprintSaveError): void {
    this.state = 'error';
    this.error = error;
    this.message = error.summary;
    this.copyStatus = null;
    this.focusErrorNext = true;
    this.host.requestUpdate();
  }

  private async copyDetails(): Promise<void> {
    if (!this.error) {
      return;
    }
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(this.error.copyText);
        this.copyStatus = 'Save error details copied.';
        this.host.requestUpdate();
        return;
      }
    } catch {
      // Fall through to manual copy.
    }

    const field = this.host.renderRoot.querySelector<HTMLTextAreaElement>('[data-wayfinder-save-error-details]');
    field?.focus();
    field?.select();
    this.copyStatus = 'Clipboard access is unavailable. Select and copy the details manually.';
    this.host.requestUpdate();
  }

  private dismissError = () => {
    this.error = null;
    this.copyStatus = null;
    this.host.requestUpdate();
  };

  renderError(): TemplateResult | typeof nothing {
    const error = this.error;
    if (!error) {
      return nothing;
    }

    const stageLink = (stageKey: string, text: string) => html`
      <button type="button" class="save-error-detail-link" data-wayfinder-save-error-jump @click=${() => this.inputs.jumpToStage(stageKey)}>
        ${text}
        <span class="save-error-detail-link-hint">Go to stage</span>
      </button>
    `;

    return html`
      <section class="save-error-surface" aria-labelledby="service-blueprint-save-error-title" tabindex="-1" data-wayfinder-save-error>
        <div class="save-error-header">
          <p class="save-error-eyebrow">Save problem</p>
          <h2 id="service-blueprint-save-error-title" class="save-error-title">${error.title}</h2>
          <p class="save-error-summary" role="alert">
            ${error.summaryStageKey ? stageLink(error.summaryStageKey, error.summary) : error.summary}
          </p>
        </div>

        ${
          error.details.length > 0
            ? html`
              <ul class="save-error-list">
                ${error.details.map((detail) => html`<li>${detail.stageKey ? stageLink(detail.stageKey, detail.message) : detail.message}</li>`)}
              </ul>
            `
            : nothing
        }

        ${error.traceId ? html`<p class="save-error-trace"><strong>Reference:</strong> ${error.traceId}</p>` : nothing}

        <label class="save-error-copy-label" for="service-blueprint-save-error-details">Copyable save error details</label>
        <textarea
          id="service-blueprint-save-error-details"
          class="save-error-copy-field"
          readonly
          rows="6"
          .value=${error.copyText}
          data-wayfinder-save-error-details
        ></textarea>

        <div class="save-error-actions">
          <button
            type="button"
            class="toolbar-btn govuk-button govuk-button--secondary save-error-copy-button"
            data-wayfinder-copy-save-error
            @click=${() => void this.copyDetails()}
          >
            Copy details
          </button>
          <button
            type="button"
            class="toolbar-btn govuk-button govuk-button--secondary"
            aria-label="Dismiss save error"
            data-wayfinder-dismiss-save-error
            @click=${this.dismissError}
          >
            Dismiss
          </button>
          <p class="save-error-copy-status" role="status" aria-live="polite" data-wayfinder-save-error-copy-status>
            ${this.copyStatus ?? ''}
          </p>
        </div>
      </section>
    `;
  }
}
