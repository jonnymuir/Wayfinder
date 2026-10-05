import { LitElement, html, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ActionCatalogEntry, ActionDefinition, SupportSystemDescriptor } from './types.js';
import { findCatalogEntry, type ActionEditorTarget } from './action-editing.js';
import type { FieldReference } from './component-property-references.js';
import { ActionDialogsController } from './action-editor/action-dialogs-controller.js';
import { ActionListController } from './action-editor/action-list-controller.js';
import type { ActionEditorHost } from './action-editor/action-editor-host.js';
import './wayfinder-inline-help.js';
import stageActionEditorStyles from './wayfinder-stage-action-editor.css?inline';

type ActionsUpdatedDetail = {
  actions: ActionDefinition[];
};

type ActionSelectedDetail = {
  index: number | null;
  target: ActionEditorTarget;
};

/**
 * @internal Composition detail of <wayfinder-service-blueprint-editor>; not part of the public API surface.
 */
@customElement('wayfinder-stage-action-editor')
export class WayfinderServiceBlueprintActionEditorElement extends LitElement implements ActionEditorHost {
  @property({ attribute: false })
  actions: ActionDefinition[] = [];

  @property({ attribute: false })
  actionCatalog: ActionCatalogEntry[] = [];

  /** Live registered support systems — drives the support-system-call action's own dedicated editor. See support-system-catalog.ts. */
  @property({ attribute: false })
  supportSystemCatalog: SupportSystemDescriptor[] = [];

  /**
   * Blueprint-wide captured input fields, for a support-system-call action's own field-ref
   * inputs. Deliberately blueprint-wide, not stage-scoped: unlike a component's own `field-ref`
   * property (checked against the *same stage*'s submitted values —
   * FieldValueValidator.cs), a capability input is typically bound to a field captured on an
   * *earlier* stage than the one carrying the action — mirrors ServiceBlueprint.ValidateSupportSystemActions'
   * own blueprint-wide field lookup, not component-property-references.ts's stage-scoped
   * `siblingFields`.
   */
  @property({ attribute: false })
  supportSystemFieldReferences: FieldReference[] = [];

  @property({ type: String })
  target: ActionEditorTarget = 'stage';

  @property({ type: String, attribute: 'subject-label' })
  subjectLabel = 'stage';

  @property({ type: Number, attribute: false })
  selectedActionIndex: number | null = null;

  @state() private _statusMessage: string | null = null;

  private readonly _list: ActionListController = new ActionListController(this, {
    requestDelete: (index, trigger) => this._dialogs.openDelete(index, trigger),
  });
  private readonly _dialogs: ActionDialogsController = new ActionDialogsController(this, {
    add: (action, label) => this._list.append(action, label),
    remove: (index) => this._list.remove(index),
  });

  protected updated(changed: Map<string, unknown>) {
    if (changed.has('selectedActionIndex') && this.selectedActionIndex !== null) {
      // Focus now, in the same task as the render that created the row: deferring to a later frame
      // lets focus be stolen from wherever the user has moved to in the meantime.
      this._focusActionEditor(this.selectedActionIndex);
    }

    if (changed.has('actions') && this.selectedActionIndex !== null && this.selectedActionIndex >= this.actions.length) {
      this.setSelectedAction(this.actions.length > 0 ? this.actions.length - 1 : null);
    }
  }

  emitActionsUpdated(actions: ActionDefinition[]) {
    // Adopt the new list straight away: the parent only hands it back after its own re-render, and
    // a second edit made before then (a fast paste, autofill) would otherwise be built on the
    // stale list and silently discard this one.
    this.actions = actions;
    this.dispatchEvent(
      new CustomEvent<ActionsUpdatedDetail>('actions-updated', {
        detail: { actions },
        bubbles: true,
        composed: true,
      })
    );
  }

  announce(message: string) {
    this._statusMessage = '';
    requestAnimationFrame(() => {
      this._statusMessage = message;
    });
  }

  setSelectedAction(index: number | null) {
    this.selectedActionIndex = index;
    this.dispatchEvent(
      new CustomEvent<ActionSelectedDetail>('action-selected', {
        detail: { index, target: this.target },
        bubbles: true,
        composed: true,
      })
    );
  }

  private _focusActionEditor(index: number) {
    const row = this.shadowRoot?.querySelector<HTMLElement>(`[data-wayfinder-stage-action="${index}"]`);
    if (!row) {
      return;
    }

    const firstField =
      row.querySelector<HTMLElement>(`[data-wayfinder-action-param^="${index}-"], [data-wayfinder-stage-action-timing="${index}"]`) ??
      row.querySelector<HTMLElement>('input, select, textarea') ??
      row.querySelector<HTMLElement>('button:not([disabled])');
    row.scrollIntoView({ block: 'nearest' });
    (firstField ?? row).focus();
  }

  actionEntry(action: ActionDefinition) {
    return findCatalogEntry(this.actionCatalog, action.type);
  }

  actionLabel(action: ActionDefinition) {
    return this.actionEntry(action)?.label ?? action.summary ?? action.type;
  }

  render() {
    return html`
      <div class="service-blueprint-action-editor">
        <div class="sr-only" role="status" aria-live="polite" aria-atomic="true">${this._statusMessage ?? ''}</div>
        <div class="section-header-row">
          <div>
            <p class="section-copy">
              ${
                this.target === 'stage'
                  ? 'Pick actions by stage context, then configure typed parameters or forms-backed fields.'
                  : 'Pick transition actions and configure their parameters here.'
              }
            </p>
          </div>
          <button type="button" class="secondary-button" data-wayfinder-open-action-picker @click=${(event: Event) => this._dialogs.openPicker(event.currentTarget as HTMLElement)}>
            Add action
          </button>
        </div>
        ${this._list.render()}
        ${this._dialogs.render()}
      </div>
    `;
  }

  static styles = unsafeCSS(stageActionEditorStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-stage-action-editor': WayfinderServiceBlueprintActionEditorElement;
  }
}
