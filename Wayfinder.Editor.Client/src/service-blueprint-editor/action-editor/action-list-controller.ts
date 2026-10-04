import type { ReactiveController } from 'lit';
import { html } from 'lit';
import type { ActionDefinition } from '../types.js';
import { availableContexts, contextForTiming, contextLabel, timingForContext, updateActionSummary } from '../action-editing.js';
import { renderActionParameters } from './action-parameter-editors.js';
import type { ActionEditorHost, ParamEditing } from './action-editor-host.js';

const ARROW_DIRECTION: Record<string, -1 | 1 | undefined> = { ArrowUp: -1, ArrowDown: 1 };

export interface ActionListContext {
  /** Asks for the delete confirmation dialog for the action at `index`. */
  requestDelete(index: number, trigger?: HTMLElement | null): void;
}

/**
 * The action list: its rows, and every change to it — editing an action's timing or parameters,
 * adding, removing and reordering (by buttons, keyboard or drag). Reports each change to the host,
 * which emits it, and keeps the selected action following the one it was on.
 */
export class ActionListController implements ReactiveController, ParamEditing {
  private _dragged: number | null = null;
  private _dragOver: number | null = null;

  constructor(
    private readonly _host: ActionEditorHost,
    private readonly _context: ActionListContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  announce(message: string) {
    this._host.announce(message);
  }

  // ── editing one action ────────────────────────────────────────────────────

  private _update(index: number, next: ActionDefinition) {
    const actions = [...this._host.actions];
    if (!actions[index]) {
      return;
    }
    actions[index] = updateActionSummary(this._host.actionEntry(next), next);
    this._host.emitActionsUpdated(actions);
  }

  updateParams(index: number, params: Record<string, unknown>) {
    const action = this._host.actions[index];
    if (action) {
      this._update(index, { ...action, params });
    }
  }

  updateParam(index: number, key: string, value: unknown) {
    const action = this._host.actions[index];
    if (action) {
      this.updateParams(index, { ...(action.params ?? {}), [key]: value });
    }
  }

  private _updateTiming(index: number, event: Event) {
    const action = this._host.actions[index];
    if (action) {
      this._update(index, { ...action, timing: (event.currentTarget as HTMLSelectElement).value as ActionDefinition['timing'] });
    }
  }

  // ── changing the list ─────────────────────────────────────────────────────

  append(action: ActionDefinition, label: string) {
    const position = this._host.actions.length;
    this._host.emitActionsUpdated([...this._host.actions, action]);
    this._host.setSelectedAction(position);
    this._host.announce(`${label} added to ${this._host.subjectLabel}.`);
  }

  remove(index: number) {
    const actions = [...this._host.actions];
    const [removed] = actions.splice(index, 1);
    this._host.emitActionsUpdated(actions);

    const selected = this._host.selectedActionIndex;
    if (selected === index) {
      this._host.setSelectedAction(actions.length > 0 ? Math.max(index - 1, 0) : null);
    } else if (selected !== null && selected > index) {
      this._host.setSelectedAction(selected - 1);
    }
    if (removed) {
      this._host.announce(`${this._host.actionLabel(removed)} removed.`);
    }
  }

  private _move(index: number, delta: -1 | 1) {
    const to = Math.min(this._host.actions.length - 1, Math.max(0, index + delta));
    if (to === index) {
      return;
    }

    const actions = [...this._host.actions];
    const [action] = actions.splice(index, 1);
    actions.splice(to, 0, action);
    this._host.emitActionsUpdated(actions);

    const selected = this._host.selectedActionIndex;
    if (selected === index) {
      this._host.setSelectedAction(to);
    } else if (selected === to) {
      this._host.setSelectedAction(index);
    }
    this._host.announce(`${this._host.actionLabel(action)} moved to position ${to + 1}.`);
  }

  private _reorder(from: number, to: number) {
    this._endDrag();
    if (from === to) {
      return;
    }

    const actions = [...this._host.actions];
    const [action] = actions.splice(from, 1);
    actions.splice(to, 0, action);
    this._host.emitActionsUpdated(actions);
    if (this._host.selectedActionIndex === from) {
      this._host.setSelectedAction(to);
    }
    this._host.announce(`${this._host.actionLabel(action)} reordered.`);
  }

  private _endDrag() {
    this._dragged = null;
    this._dragOver = null;
    this._host.requestUpdate();
  }

  // ── rows ──────────────────────────────────────────────────────────────────

  private _rowKeydown(event: KeyboardEvent, index: number) {
    const direction = event.altKey ? ARROW_DIRECTION[event.key] : undefined;
    if (direction) {
      event.preventDefault();
      this._move(index, direction);
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      this._context.requestDelete(index);
    }
  }

  private _renderTiming(action: ActionDefinition, index: number) {
    if (this._host.target !== 'stage') {
      return html`<span class="action-context-pill">${contextLabel('transition')}</span>`;
    }

    const entry = this._host.actionEntry(action);
    const contexts = entry ? availableContexts(entry, this._host.target) : [];
    return html`
      <label class="field-block compact-field">
        <span class="field-label">Timing</span>
        <select class="field-control" data-wayfinder-stage-action-timing="${index}" @change=${(event: Event) => this._updateTiming(index, event)}>
          ${contexts.map(
            (context) => html`
              <option value=${timingForContext(context)} ?selected=${contextForTiming(action.timing, this._host.target) === context}>
                ${contextLabel(context)}
              </option>
            `
          )}
        </select>
      </label>
    `;
  }

  private _renderRow(action: ActionDefinition, index: number) {
    const entry = this._host.actionEntry(action);
    const selected = this._host.selectedActionIndex === index;
    return html`
      <li
        class="action-item ${this._dragOver === index ? 'action-item-drop' : ''} ${selected ? 'action-item-selected' : ''}"
        data-wayfinder-stage-action="${index}"
        data-wayfinder-action-selected=${String(selected)}
        tabindex="0"
        @click=${() => this._host.setSelectedAction(index)}
        @focusin=${() => this._host.setSelectedAction(index)}
        @keydown=${(event: KeyboardEvent) => this._rowKeydown(event, index)}
        @dragover=${(event: DragEvent) => {
          if (this._dragged !== null && this._dragged !== index) {
            event.preventDefault();
            this._dragOver = index;
            this._host.requestUpdate();
          }
        }}
        @drop=${(event: DragEvent) => {
          event.preventDefault();
          if (this._dragged !== null) {
            this._reorder(this._dragged, index);
          }
        }}
      >
        <div class="action-item-main">
          <button
            type="button"
            class="drag-button"
            draggable="true"
            aria-label=${`Drag ${this._host.actionLabel(action)} to reorder`}
            @dragstart=${() => {
              this._dragged = index;
              this._dragOver = null;
              this._host.requestUpdate();
            }}
            @dragend=${() => this._endDrag()}
          >
            ↕
          </button>
          <div class="action-copy">
            <p class="action-title">${this._host.actionLabel(action)}</p>
            <p class="action-summary">${action.summary ?? entry?.summary ?? action.type}</p>
          </div>
        </div>
        <div class="action-item-controls">
          ${this._renderTiming(action, index)}
          <div class="action-buttons">
            <button type="button" class="icon-button" ?disabled=${index === 0} @click=${() => this._move(index, -1)}>Move up</button>
            <button type="button" class="icon-button" ?disabled=${index === this._host.actions.length - 1} @click=${() => this._move(index, 1)}>Move down</button>
            <button
              type="button"
              class="icon-button danger-button"
              data-wayfinder-stage-action-remove="${index}"
              @click=${(event: Event) => this._context.requestDelete(index, event.currentTarget as HTMLElement)}
            >
              Remove
            </button>
          </div>
        </div>
        ${renderActionParameters(this._host, this, index)}
      </li>
    `;
  }

  render() {
    const { actions, subjectLabel } = this._host;
    return actions.length === 0
      ? html`<p class="section-empty">No actions configured for this ${subjectLabel}.</p>`
      : html`<ol class="action-list">${actions.map((action, index) => this._renderRow(action, index))}</ol>`;
  }
}
