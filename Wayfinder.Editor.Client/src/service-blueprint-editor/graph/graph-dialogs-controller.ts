import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { nothing } from 'lit';
import { html, unsafeStatic } from 'lit/static-html.js';
import type { RouteView, ServiceBlueprint, ServiceBlueprintGatewayDefinition, StageKind } from '../types.js';
import { applyQueueToStage, stageQueueKey, type StageSurface } from '../stage-assignment.js';
import { STAGE_KIND_OPTIONS } from '../stage-kind-options.js';
import { trapDialogFocus } from '../dialog-focus-trap.js';
import {
  insertGateway,
  insertStage,
  removeNode,
  routesTouching,
  slugifyGatewayKey,
  uniqueStageKey,
  usedNodeKeys,
  type StagePlacement,
} from './graph-structure-edits.js';

type NodeKind = 'stage' | 'gateway';

type Dialog =
  | {
      kind: 'create-stage';
      placement: StagePlacement;
      title: string;
      stageKey: string;
      queueKey: string;
      stageType: StageKind;
      keyTouched: boolean;
      error: string | null;
    }
  | {
      kind: 'create-gateway';
      title: string;
      gatewayKey: string;
      gatewayType: 'Split' | 'Join';
      queueKey: string;
      keyTouched: boolean;
      error: string | null;
    }
  | { kind: 'delete'; node: NodeKind; key: string; affectedRoutes: RouteView[] };

/** What the dialogs need from the graph that hosts them; all reads are current-value lookups. */
export interface GraphDialogsContext {
  blueprint(): ServiceBlueprint | null;
  queueKeys(): string[];
  queueLabel(queueKey: string): string;
  nodeLabel(nodeKey: string): string;
  /** Where focus returns when a dialog opened without an explicit trigger closes (the context menu's). */
  fallbackReturnTarget(): HTMLElement | null;
  dismissContextMenu(): void;
  announce(message: string): void;
  stageCreated(blueprint: ServiceBlueprint, stageKey: string): void;
  gatewayCreated(blueprint: ServiceBlueprint, gatewayKey: string): void;
  nodeDeleted(blueprint: ServiceBlueprint, node: NodeKind): void;
}

function defaultQueueForSurface(surface: StageSurface): string {
  return surface === 'back-stage' ? 'reviewer' : 'public';
}

/**
 * The graph's modal dialogs — create stage, create gateway, delete stage/gateway. Owns which one
 * is open, its draft state, validation, the resulting blueprint edit (via graph-structure-edits)
 * and the markup, so the graph element only reacts to the finished edit through the context.
 */
export class GraphDialogsController implements ReactiveController {
  private _dialog: Dialog | null = null;
  private _returnTarget: HTMLElement | null = null;

  constructor(
    private readonly _host: ReactiveControllerHost & HTMLElement,
    private readonly _context: GraphDialogsContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  openCreateStage(surface: StageSurface, placement: StagePlacement, returnTarget?: HTMLElement | null) {
    const blueprint = this._context.blueprint();
    const referenceStage =
      placement.position === 'append' ? null : (blueprint?.stages.find((stage) => stage.stageKey === placement.referenceStageKey) ?? null);
    const title = 'New stage';
    this._open(
      {
        kind: 'create-stage',
        placement,
        title,
        stageKey: uniqueStageKey(blueprint, title, 'new-stage'),
        queueKey: referenceStage ? stageQueueKey(referenceStage) : defaultQueueForSurface(surface),
        stageType: 'Question',
        keyTouched: false,
        error: null,
      },
      returnTarget,
      '[data-wayfinder-create-stage-title]'
    );
  }

  openCreateGateway(returnTarget?: HTMLElement | null) {
    const blueprint = this._context.blueprint();
    if (!blueprint) {
      return;
    }
    // Prefer a queue an existing stage already lives in: defaulting to the first host-supplied
    // queue can pick one the blueprint never uses, silently creating a same-labelled duplicate
    // lane (lanes group by key, not label).
    const queueKey = stageQueueKey(blueprint.stages[0]) || this._context.queueKeys()[0] || 'public';
    this._open(
      { kind: 'create-gateway', title: '', gatewayKey: '', gatewayType: 'Split', queueKey, keyTouched: false, error: null },
      returnTarget,
      '[data-wayfinder-create-gateway-title]'
    );
  }

  openDelete(node: NodeKind, key: string, returnTarget?: HTMLElement | null) {
    const blueprint = this._context.blueprint();
    if (!blueprint) {
      return;
    }
    this._open(
      { kind: 'delete', node, key, affectedRoutes: routesTouching(blueprint, key) },
      returnTarget,
      `[data-wayfinder-delete-${node}-cancel]`
    );
  }

  render() {
    const dialog = this._dialog;
    if (!dialog) {
      return nothing;
    }
    switch (dialog.kind) {
      case 'create-stage':
        return this._renderCreateStage(dialog);
      case 'create-gateway':
        return this._renderCreateGateway(dialog);
      case 'delete':
        return this._renderDelete(dialog);
    }
  }

  private _open(dialog: Dialog, returnTarget: HTMLElement | null | undefined, focusSelector: string) {
    this._returnTarget = returnTarget ?? this._context.fallbackReturnTarget();
    this._dialog = dialog;
    this._context.dismissContextMenu();
    this._host.requestUpdate();
    requestAnimationFrame(() => this._host.shadowRoot?.querySelector<HTMLElement>(focusSelector)?.focus());
  }

  private readonly _close = () => {
    this._dialog = null;
    const returnTarget = this._returnTarget;
    this._returnTarget = null;
    this._host.requestUpdate();
    requestAnimationFrame(() => returnTarget?.focus());
  };

  private _patch(changes: Partial<Dialog>) {
    if (this._dialog) {
      this._dialog = { ...this._dialog, ...changes } as Dialog;
      this._host.requestUpdate();
    }
  }

  private _fail(error: string) {
    this._patch({ error } as Partial<Dialog>);
  }

  private readonly _submitCreateStage = () => {
    const dialog = this._dialog;
    const blueprint = this._context.blueprint();
    if (!blueprint || dialog?.kind !== 'create-stage') {
      return;
    }

    const title = dialog.title.trim();
    const stageKey = dialog.stageKey.trim().toLowerCase();
    if (!title) {
      return this._fail('Stage name is required.');
    }
    if (!stageKey) {
      return this._fail('Stage key is required.');
    }
    if (blueprint.stages.some((stage) => stage.stageKey === stageKey)) {
      return this._fail('Stage key must be unique.');
    }

    const stage = applyQueueToStage(
      { stageKey, displayName: title, components: [], stageType: dialog.stageType, queueKey: '', actions: [], roleGates: [] },
      dialog.queueKey
    );
    this._context.announce(`${title} added to the workspace.`);
    this._close();
    this._context.stageCreated(insertStage(blueprint, stage, dialog.placement), stageKey);
  };

  private readonly _submitCreateGateway = () => {
    const dialog = this._dialog;
    const blueprint = this._context.blueprint();
    if (!blueprint || dialog?.kind !== 'create-gateway') {
      return;
    }

    const title = dialog.title.trim();
    const key = dialog.gatewayKey.trim();
    if (!title) {
      return this._fail('Gateway name is required.');
    }
    if (!key) {
      return this._fail('Gateway key is required.');
    }
    if (usedNodeKeys(blueprint).has(key)) {
      return this._fail('Gateway key must be unique across all stages and gateways.');
    }

    const gateway: ServiceBlueprintGatewayDefinition = {
      key,
      displayName: title,
      gatewayType: dialog.gatewayType,
      queueKey: dialog.queueKey,
      actor: dialog.queueKey,
      roleGates: [],
    };
    this._context.announce(`${title} ${dialog.gatewayType} gateway created.`);
    this._close();
    this._context.gatewayCreated(insertGateway(blueprint, gateway), key);
  };

  private readonly _confirmDelete = () => {
    const dialog = this._dialog;
    const blueprint = this._context.blueprint();
    if (!blueprint || dialog?.kind !== 'delete') {
      return;
    }

    const count = dialog.affectedRoutes.length;
    this._context.announce(
      `${this._context.nodeLabel(dialog.key)} deleted.${count > 0 ? ` ${count} affected transition${count === 1 ? '' : 's'} removed.` : ''}`
    );
    this._close();
    this._context.nodeDeleted(removeNode(blueprint, dialog.key), dialog.node);
  };

  private _renderQueueDatalist(id: string) {
    return html`<datalist id=${id}>
      ${this._context.queueKeys().map((option) => html`<option value=${option}>${this._context.queueLabel(option)}</option>`)}
    </datalist>`;
  }

  private _renderCreateStage(dialog: Extract<Dialog, { kind: 'create-stage' }>) {
    const input = (event: Event) => (event.currentTarget as HTMLInputElement).value;
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel"
          role="dialog"
          aria-modal="true"
          aria-labelledby="create-stage-dialog-title"
          aria-describedby="create-stage-dialog-copy"
          data-wayfinder-create-stage-dialog
          @keydown=${(event: KeyboardEvent) => trapDialogFocus(event, () => this._host.shadowRoot?.activeElement, this._close)}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow">Stage creation</p>
              <h2 id="create-stage-dialog-title" class="dialog-title">Create stage</h2>
            </div>
          </div>
          <p id="create-stage-dialog-copy" class="dialog-copy">
            Name the stage, choose its key, queue, and type, then continue editing in the inspector.
          </p>
          ${dialog.error ? html`<p class="dialog-error" data-wayfinder-create-stage-error>${dialog.error}</p>` : nothing}
          <div class="dialog-grid">
            <label class="dialog-field">
              <span class="dialog-label">Name</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-title
                .value=${dialog.title}
                @input=${(event: Event) => {
                  const title = input(event);
                  this._patch({
                    title,
                    stageKey: dialog.keyTouched ? dialog.stageKey : uniqueStageKey(this._context.blueprint(), title, 'new-stage'),
                    error: null,
                  });
                }}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Key</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-key
                .value=${dialog.stageKey}
                @input=${(event: Event) => this._patch({ stageKey: input(event), keyTouched: true, error: null })}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Queue</span>
              <input
                class="dialog-control"
                data-wayfinder-create-stage-queue
                .value=${dialog.queueKey}
                list="create-stage-queue-options"
                placeholder="planning"
                @input=${(event: Event) => this._patch({ queueKey: input(event), error: null })}
              />
              ${this._renderQueueDatalist('create-stage-queue-options')}
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Type</span>
              <select
                class="dialog-control"
                data-wayfinder-create-stage-type
                @change=${(event: Event) => this._patch({ stageType: (event.currentTarget as HTMLSelectElement).value as StageKind })}
              >
                ${STAGE_KIND_OPTIONS.map(
                  (option) => html`<option value=${option.value} ?selected=${dialog.stageType === option.value}>${option.label}</option>`
                )}
              </select>
            </label>
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" @click=${this._close}>Cancel</button>
            <button type="button" class="dialog-button primary" data-wayfinder-create-stage-submit @click=${this._submitCreateStage}>Create stage</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderCreateGateway(dialog: Extract<Dialog, { kind: 'create-gateway' }>) {
    const input = (event: Event) => (event.currentTarget as HTMLInputElement).value;
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel"
          role="dialog"
          aria-modal="true"
          aria-labelledby="create-gateway-dialog-title"
          aria-describedby="create-gateway-dialog-copy"
          data-wayfinder-create-gateway-dialog
          @keydown=${(event: KeyboardEvent) => trapDialogFocus(event, () => this._host.shadowRoot?.activeElement, this._close)}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow">Gateway creation</p>
              <h2 id="create-gateway-dialog-title" class="dialog-title">Add gateway</h2>
            </div>
          </div>
          <p id="create-gateway-dialog-copy" class="dialog-copy">
            Add a Split or Join gateway to the workspace. Continue editing in the inspector after creation.
          </p>
          ${dialog.error ? html`<p class="dialog-error" data-wayfinder-create-gateway-error>${dialog.error}</p>` : nothing}
          <div class="dialog-grid">
            <label class="dialog-field">
              <span class="dialog-label">Name</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-title
                .value=${dialog.title}
                @input=${(event: Event) => {
                  const title = input(event);
                  this._patch({ title, gatewayKey: dialog.keyTouched ? dialog.gatewayKey : slugifyGatewayKey(title), error: null });
                }}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Key</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-key
                .value=${dialog.gatewayKey}
                @input=${(event: Event) => this._patch({ gatewayKey: input(event), keyTouched: true, error: null })}
              />
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Kind</span>
              <select
                class="dialog-control"
                data-wayfinder-create-gateway-kind
                @change=${(event: Event) => this._patch({ gatewayType: (event.currentTarget as HTMLSelectElement).value as 'Split' | 'Join' })}
              >
                <option value="Split" ?selected=${dialog.gatewayType === 'Split'}>Split — branches into multiple paths</option>
                <option value="Join" ?selected=${dialog.gatewayType === 'Join'}>Join — converges multiple paths</option>
              </select>
            </label>
            <label class="dialog-field">
              <span class="dialog-label">Queue</span>
              <input
                class="dialog-control"
                data-wayfinder-create-gateway-queue
                .value=${dialog.queueKey}
                list="create-gateway-queue-options"
                placeholder="applicant"
                @input=${(event: Event) => this._patch({ queueKey: input(event) })}
              />
              ${this._renderQueueDatalist('create-gateway-queue-options')}
            </label>
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" @click=${this._close}>Cancel</button>
            <button type="button" class="dialog-button primary" data-wayfinder-create-gateway-submit @click=${this._submitCreateGateway}>Create gateway</button>
          </div>
        </div>
      </div>
    `;
  }

  private _renderDelete(dialog: Extract<Dialog, { kind: 'delete' }>) {
    const node = unsafeStatic(dialog.node);
    const label = this._context.nodeLabel(dialog.key);
    const count = dialog.affectedRoutes.length;
    return html`
      <div class="dialog-backdrop" role="presentation">
        <div
          class="dialog-panel dialog-panel-danger"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-${node}-dialog-title"
          aria-describedby="delete-${node}-dialog-copy"
          data-wayfinder-delete-${node}-dialog
          @keydown=${(event: KeyboardEvent) => trapDialogFocus(event, () => this._host.shadowRoot?.activeElement, this._close)}
        >
          <div class="dialog-header">
            <div>
              <p class="dialog-eyebrow danger">Delete ${node}</p>
              <h2 id="delete-${node}-dialog-title" class="dialog-title">Delete ${label}?</h2>
            </div>
          </div>
          <p id="delete-${node}-dialog-copy" class="dialog-copy">
            This removes the ${node} and every transition connected to it.
          </p>
          <div class="delete-impact" data-wayfinder-delete-${node}-transitions>
            ${
              count === 0
                ? html`<p>No transitions will be removed.</p>`
                : html`
                  <p>${count} affected transition${count === 1 ? '' : 's'}:</p>
                  <ul>
                    ${dialog.affectedRoutes.map(
                      (route) =>
                        html`<li>${this._context.nodeLabel(route.fromStage)} → ${this._context.nodeLabel(route.toStage)} (${route.action})</li>`
                    )}
                  </ul>
                `
            }
          </div>
          <div class="dialog-actions">
            <button type="button" class="dialog-button secondary" data-wayfinder-delete-${node}-cancel @click=${this._close}>Cancel</button>
            <button type="button" class="dialog-button danger" data-wayfinder-delete-${node}-confirm @click=${this._confirmDelete}>Delete ${node}</button>
          </div>
        </div>
      </div>
    `;
  }
}
