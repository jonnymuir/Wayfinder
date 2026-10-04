import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { ActionCatalogEntry, ActionDefinition } from '../types.js';
import {
  availableContexts,
  buildActionParams,
  contextLabel,
  timingForContext,
  updateActionSummary,
  type ActionEditorContext,
} from '../action-editing.js';
import { trapDialogFocus } from '../dialog-focus-trap.js';
import type { ActionEditorHost } from './action-editor-host.js';

type Picker = { query: string; context: ActionEditorContext; selectedType: string | null };
type DeleteTarget = { index: number; label: string };

export interface ActionDialogsContext {
  add(action: ActionDefinition, label: string): void;
  remove(index: number): void;
}

/**
 * The two dialogs of the action editor: the picker that adds an action (search, context, choose) and
 * the confirmation before removing one. Owns which is open and its state, focus management on open
 * and close, and the markup; reports the finished choice through its context.
 */
export class ActionDialogsController implements ReactiveController {
  private _picker: Picker | null = null;
  private _delete: DeleteTarget | null = null;
  private _returnTarget: HTMLElement | null = null;

  constructor(
    private readonly _host: ActionEditorHost,
    private readonly _context: ActionDialogsContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  // ── picker ────────────────────────────────────────────────────────────────

  private get _catalogEntries(): ActionCatalogEntry[] {
    return this._host.actionCatalog.filter((entry) => availableContexts(entry, this._host.target).length > 0);
  }

  private get _pickerEntries(): ActionCatalogEntry[] {
    if (!this._picker) {
      return [];
    }

    const { context, query } = this._picker;
    const needle = query.trim().toLowerCase();
    return this._catalogEntries
      .filter((entry) => entry.appliesTo.includes(context))
      .filter((entry) => !needle || [entry.label, entry.type, entry.summary].some((value) => value.toLowerCase().includes(needle)));
  }

  openPicker(trigger?: HTMLElement | null) {
    const context: ActionEditorContext = this._host.target === 'transition' ? 'transition' : 'stage.onEntry';
    const first = this._catalogEntries.find((entry) => entry.appliesTo.includes(context)) ?? this._catalogEntries[0] ?? null;
    this._open({ picker: { query: '', context, selectedType: first?.type ?? null } }, trigger, '[data-wayfinder-action-picker-search]');
  }

  private _patchPicker(change: Partial<Picker>) {
    if (this._picker) {
      this._picker = { ...this._picker, ...change };
      this._host.requestUpdate();
    }
  }

  private _addPicked() {
    const picker = this._picker;
    if (!picker) {
      return;
    }

    const entry = this._catalogEntries.find((candidate) => candidate.type === picker.selectedType) ?? this._pickerEntries[0] ?? null;
    if (!entry) {
      return;
    }

    const action = updateActionSummary(entry, {
      type: entry.type,
      timing: timingForContext(picker.context),
      parameterSchemaKey: entry.paramsSchema.key || undefined,
      params: buildActionParams(entry),
      summary: entry.summary,
    });
    this._context.add(action, entry.label);
    this._close();
  }

  // ── delete confirmation ───────────────────────────────────────────────────

  openDelete(index: number, trigger?: HTMLElement | null) {
    const action = this._host.actions[index];
    if (action) {
      this._open({ delete: { index, label: this._host.actionLabel(action) } }, trigger, '[data-wayfinder-delete-action-cancel]');
    }
  }

  private _confirmDelete() {
    const target = this._delete;
    if (target) {
      this._context.remove(target.index);
      this._close();
    }
  }

  // ── shared ────────────────────────────────────────────────────────────────

  private _open(state: { picker?: Picker; delete?: DeleteTarget }, trigger: HTMLElement | null | undefined, focusSelector: string) {
    this._returnTarget = trigger ?? null;
    this._picker = state.picker ?? null;
    this._delete = state.delete ?? null;
    this._host.requestUpdate();
    requestAnimationFrame(() => this._host.shadowRoot?.querySelector<HTMLElement>(focusSelector)?.focus());
  }

  private readonly _close = () => {
    this._picker = null;
    this._delete = null;
    const returnTarget = this._returnTarget;
    this._returnTarget = null;
    this._host.requestUpdate();
    returnTarget?.focus();
  };

  private _keydown(event: KeyboardEvent) {
    trapDialogFocus(event, () => this._host.shadowRoot?.activeElement, this._close);
  }

  render() {
    return html`${this._renderPicker()}${this._renderDelete()}`;
  }

  private _renderPicker() {
    const picker = this._picker;
    if (!picker) {
      return nothing;
    }

    const entries = this._pickerEntries;
    const contexts: ActionEditorContext[] = this._host.target === 'transition' ? ['transition'] : ['stage.onEntry', 'stage.onExit'];
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel"
          role="dialog"
          aria-modal="true"
          aria-labelledby="action-picker-title"
          data-wayfinder-action-picker-dialog
          @keydown=${(event: KeyboardEvent) => this._keydown(event)}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow">Action picker</p>
              <h4 id="action-picker-title" class="dialog-title">Add an action</h4>
            </div>
          </div>
          <div class="dialog-grid">
            <label class="dialog-field">
              <span class="dialog-label">Search</span>
              <input
                class="dialog-control"
                data-wayfinder-action-picker-search
                .value=${picker.query}
                @input=${(event: Event) => this._patchPicker({ query: (event.currentTarget as HTMLInputElement).value })}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Context</span>
              <select
                class="dialog-control"
                data-wayfinder-action-picker-context
                @change=${(event: Event) => {
                  const context = (event.currentTarget as HTMLSelectElement).value as ActionEditorContext;
                  const first = this._catalogEntries.find((entry) => entry.appliesTo.includes(context)) ?? null;
                  this._patchPicker({ context, selectedType: first?.type ?? null });
                }}
              >
                ${contexts.map((context) => html`<option value=${context} ?selected=${picker.context === context}>${contextLabel(context)}</option>`)}
              </select>
            </label>
          </div>
          <div class="picker-list" role="listbox" aria-label="Available actions">
            ${entries.map(
              (entry) => html`
                <button
                  type="button"
                  class=${`picker-option ${picker.selectedType === entry.type ? 'selected' : ''}`}
                  data-wayfinder-action-picker-option=${entry.type}
                  @click=${() => this._patchPicker({ selectedType: entry.type })}
                >
                  <span class="picker-option-title">${entry.label}</span>
                  <span class="picker-option-type">${entry.type}</span>
                  <span class="picker-option-summary">${entry.summary}</span>
                  <span class="picker-option-meta">${entry.appliesTo
                    .filter((scope) => scope !== 'transition' || this._host.target === 'transition')
                    .map((scope) => contextLabel(scope as ActionEditorContext))
                    .join(' · ')}</span>
                </button>
              `
            )}
            ${entries.length === 0 ? html`<p class="section-empty">No actions match the current filter.</p>` : nothing}
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" @click=${this._close}>Cancel</button>
            <button type="button" class="dialog-button primary" data-wayfinder-action-picker-add @click=${() => this._addPicked()} ?disabled=${entries.length === 0}>Add action</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderDelete() {
    const target = this._delete;
    if (!target) {
      return nothing;
    }

    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel dialog-panel-danger"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-action-title"
          data-wayfinder-delete-action-dialog
          @keydown=${(event: KeyboardEvent) => this._keydown(event)}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow danger">Delete action</p>
              <h4 id="delete-action-title" class="dialog-title">Delete ${target.label}?</h4>
            </div>
          </div>
          <p class="dialog-copy">This removes the action and its configuration from this ${this._host.subjectLabel}.</p>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" data-wayfinder-delete-action-cancel @click=${this._close}>Cancel</button>
            <button type="button" class="dialog-button danger" data-wayfinder-delete-action-confirm @click=${() => this._confirmDelete()}>Delete action</button>
          </div>
        </div>
      </div>
    `;
  }
}
