import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';
import type { ServiceBlueprintGatewayDefinition } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import { defaultIconForGateway, type NodeIconName } from '../graph/node-icons.js';
import { stageQueueLabel, serviceBlueprintQueueOptions } from '../stage-assignment.js';
import { deriveGatewayBindings, gatewayQueueKey } from '../gateway-representation.js';
import type { InspectorHost } from './inspector-host.js';
import type { RouteEditorController } from './route-editor-controller.js';
import { renderIconPicker } from './node-icon-picker.js';

/** The properties panel for a selected gateway: identity, queue, join-waiting behaviour, delete, and its routes. */
export class GatewayInspectorController implements ReactiveController {
  private _gatewayKeyErrorValue: string | null = null;
  private _lastGatewayKey: string | null = null;

  constructor(
    private readonly _host: InspectorHost,
    private readonly _routes: RouteEditorController
  ) {
    _host.addController(this);
  }

  private get _gatewayKeyError() {
    return this._gatewayKeyErrorValue;
  }

  private set _gatewayKeyError(value: string | null) {
    this._gatewayKeyErrorValue = value;
    this._host.requestUpdate();
  }

  /** A key error belongs to the gateway it was raised on. */
  hostUpdate() {
    if (this._host.selectedGatewayKey !== this._lastGatewayKey) {
      this._lastGatewayKey = this._host.selectedGatewayKey;
      this._gatewayKeyErrorValue = null;
    }
  }

  private _replaceSelectedGateway(nextGateway: ServiceBlueprintGatewayDefinition, previousGatewayKey = this._host.selectedGateway?.key) {
    if (!this._host.serviceBlueprint || !previousGatewayKey) {
      return;
    }

    const gatewayIndex = serviceBlueprintGateways(this._host.serviceBlueprint).findIndex((g) => g.key === previousGatewayKey);
    if (gatewayIndex < 0) {
      return;
    }

    const gateways = [...serviceBlueprintGateways(this._host.serviceBlueprint)];
    gateways[gatewayIndex] = nextGateway;

    let nextStates = this._host.serviceBlueprint.stages;
    let nextGateways = gateways;
    if (nextGateway.key !== previousGatewayKey) {
      nextStates = this._host.serviceBlueprint.stages.map((stage) => ({
        ...stage,
        routes: (stage.routes ?? []).map((route) => ({
          ...route,
          target: route.target === previousGatewayKey ? nextGateway.key : route.target,
        })),
      }));
      nextGateways = gateways.map((g, idx) =>
        idx === gatewayIndex
          ? g
          : {
              ...g,
              routes: (g.routes ?? []).map((route) => ({
                ...route,
                target: route.target === previousGatewayKey ? nextGateway.key : route.target,
              })),
            }
      );
    }

    this._host.emitUpdated(
      { ...this._host.serviceBlueprint, stages: nextStates, gateways: nextGateways },
      { kind: 'gateway', gatewayKey: nextGateway.key }
    );
  }

  private readonly _updateGatewayDisplayName = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (!gateway) return;
    const nextName = (event.currentTarget as HTMLInputElement).value.trim();
    if (!nextName || nextName === gateway.displayName) return;
    this._replaceSelectedGateway({ ...gateway, displayName: nextName });
    this._host.announce(`${nextName} gateway name updated.`);
  };

  private _updateGatewayIcon(gateway: ServiceBlueprintGatewayDefinition, iconName: NodeIconName) {
    if (gateway.icon === iconName) {
      return;
    }
    this._replaceSelectedGateway({ ...gateway, icon: iconName });
    this._host.announce(`${gateway.displayName} icon updated.`);
  }

  private readonly _updateGatewayKey = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (!gateway || !this._host.serviceBlueprint) return;
    const nextKey = (event.currentTarget as HTMLInputElement).value.trim();
    if (!nextKey) {
      this._gatewayKeyError = 'Gateway key is required.';
      this._host.announce('Gateway key is required.');
      return;
    }

    const allKeys = [
      ...this._host.serviceBlueprint.stages.map((s) => s.stageKey),
      ...serviceBlueprintGateways(this._host.serviceBlueprint)
        .map((g) => g.key)
        .filter((k) => k !== gateway.key),
    ];
    if (allKeys.includes(nextKey)) {
      this._gatewayKeyError = 'Gateway key must be unique across stages and gateways.';
      this._host.announce(`Key ${nextKey} is already in use.`);
      return;
    }

    if (nextKey === gateway.key) {
      this._gatewayKeyError = null;
      return;
    }

    this._gatewayKeyError = null;
    this._replaceSelectedGateway({ ...gateway, key: nextKey }, gateway.key);
    this._host.announce(`Gateway key updated to ${nextKey}.`);
  };

  private readonly _updateGatewayQueue = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (!gateway) return;
    const queueKey = (event.currentTarget as HTMLInputElement).value.trim();
    if (!queueKey || queueKey === gatewayQueueKey(gateway)) return;
    this._replaceSelectedGateway({ ...gateway, queueKey, actor: queueKey.includes('business') ? 'reviewer' : queueKey });
    this._host.announce(`${gateway.displayName} queue updated to ${queueKey}.`);
  };

  private readonly _updateGatewayDescription = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (!gateway) return;
    const nextDesc = (event.currentTarget as HTMLTextAreaElement).value.trim();
    if ((nextDesc || undefined) === (gateway.description?.trim() || undefined)) return;
    this._replaceSelectedGateway({ ...gateway, description: nextDesc || undefined });
    this._host.announce(`${gateway.displayName} description updated.`);
  };

  private readonly _updateJoinWaitingContent = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (gateway?.gatewayType !== 'Join') return;
    const content = (event.currentTarget as HTMLTextAreaElement).value.trim() || undefined;
    this._replaceSelectedGateway({ ...gateway, waitingContent: content });
    this._host.announce(`${gateway.displayName} waiting message updated.`);
  };

  private readonly _updateJoinWaitingExpectedSeconds = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (gateway?.gatewayType !== 'Join') return;
    const raw = (event.currentTarget as HTMLInputElement).value;
    const expectedWaitSeconds = raw ? Number(raw) : undefined;
    this._replaceSelectedGateway({ ...gateway, waitingExpectedSeconds: expectedWaitSeconds });
    this._host.announce(`${gateway.displayName} expected wait updated.`);
  };

  private readonly _updateJoinWaitingAllowDefer = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (gateway?.gatewayType !== 'Join') return;
    const allowDefer = (event.currentTarget as HTMLInputElement).checked;
    this._replaceSelectedGateway({
      ...gateway,
      waitingAllowDefer: allowDefer,
      waitingDeferMessage: allowDefer ? gateway.waitingDeferMessage : undefined,
    });
    this._host.announce(allowDefer ? `${gateway.displayName} defer enabled.` : `${gateway.displayName} defer disabled.`);
  };

  private readonly _updateJoinWaitingDeferMessage = (event: Event) => {
    const gateway = this._host.selectedGateway;
    if (gateway?.gatewayType !== 'Join') return;
    const deferMessage = (event.currentTarget as HTMLInputElement).value.trim() || undefined;
    this._replaceSelectedGateway({ ...gateway, waitingDeferMessage: deferMessage });
    this._host.announce(`${gateway.displayName} defer message updated.`);
  };

  private readonly _deleteSelectedGateway = () => {
    const gateway = this._host.selectedGateway;
    if (!this._host.serviceBlueprint || !gateway) return;
    const gateways = serviceBlueprintGateways(this._host.serviceBlueprint).filter((g) => g.key !== gateway.key);
    const nextServiceBlueprint = {
      ...this._host.serviceBlueprint,
      stages: this._host.serviceBlueprint.stages.map((stage) => ({
        ...stage,
        routes: (stage.routes ?? []).filter((route) => route.target !== gateway.key),
      })),
      gateways: gateways.map((candidate) => ({
        ...candidate,
        routes: (candidate.routes ?? []).filter((route) => route.target !== gateway.key),
      })),
    };
    this._host.emitUpdated(nextServiceBlueprint, null);
    this._host.announce(`${gateway.displayName} gateway deleted.`);
  };

  renderGateway(gateway: ServiceBlueprintGatewayDefinition) {
    const queueKey = gatewayQueueKey(gateway);
    const queueLabel = stageQueueLabel(this._host.serviceBlueprint, queueKey, this._host.availableQueues);
    const binding = this._host.serviceBlueprint
      ? (deriveGatewayBindings(this._host.serviceBlueprint).find((candidate) => candidate.gateway.key === gateway.key) ?? null)
      : null;
    const queueOptionsId = `gateway-queue-options-${gateway.key}`;
    const isJoin = gateway.gatewayType === 'Join';

    return html`
      <article
        class="inspector-panel"
        data-wayfinder-gateway-detail="${gateway.key}"
        data-wayfinder-inspector-kind="gateway"
        aria-labelledby="inspector-gateway-title"
      >
        <div class="inspector-header">
          <div>
            <p class="eyebrow">${queueLabel} queue</p>
            <h2 id="inspector-gateway-title" class="stage-title" data-wayfinder-inspector-heading>${gateway.displayName}</h2>
          </div>
          <span class="stage-kind-badge transition-badge" data-wayfinder-field="kind">${gateway.gatewayType} gateway</span>
        </div>

        <section class="inspector-section" aria-labelledby="gateway-basics-heading">
          <h3 id="gateway-basics-heading" class="section-heading">Gateway details</h3>
          <div class="field-grid">
            <label class="field-block">
              <span class="field-label">Name</span>
              <input
                class="field-control"
                data-wayfinder-gateway-name
                .value=${gateway.displayName}
                @change=${this._updateGatewayDisplayName}
              />
            </label>
            <label class="field-block">
              <span class="field-label-row">
                <span class="field-label">Key</span>
                <wayfinder-inline-help
                  label="Gateway key help"
                  message="A stable, unique identifier for this gateway. Must not clash with any stage key or other gateway key. Route bindings reference this key."
                ></wayfinder-inline-help>
              </span>
              <input
                class="field-control ${this._gatewayKeyError ? 'field-control-error' : ''}"
                data-wayfinder-gateway-key
                aria-invalid=${String(Boolean(this._gatewayKeyError))}
                .value=${gateway.key}
                @input=${() => {
                  this._gatewayKeyError = null;
                }}
                @change=${this._updateGatewayKey}
              />
              ${
                this._gatewayKeyError
                  ? html`<span class="field-error" data-wayfinder-gateway-key-error>${this._gatewayKeyError}</span>`
                  : nothing
              }
            </label>
            <label class="field-block">
              <span class="field-label-row">
                <span class="field-label">Queue</span>
                <wayfinder-inline-help
                  label="Queue help"
                  message="The queue that owns this gateway. For a join gateway, the owning queue is where waiting information is shown to users."
                ></wayfinder-inline-help>
              </span>
              <input
                class="field-control"
                data-wayfinder-gateway-queue
                .value=${queueKey}
                list=${queueOptionsId}
                placeholder="applicant"
                @change=${this._updateGatewayQueue}
              />
              <datalist id=${queueOptionsId}>
                ${serviceBlueprintQueueOptions(this._host.serviceBlueprint, this._host.availableQueues).map(
                  (option) => html`
                  <option value=${option}>${stageQueueLabel(this._host.serviceBlueprint, option, this._host.availableQueues)}</option>
                `
                )}
              </datalist>
            </label>
          </div>

          <div class="field-block field-block-full">
            <span class="field-label">Icon</span>
            ${renderIconPicker(gateway.icon ?? defaultIconForGateway(gateway), (iconName) => this._updateGatewayIcon(gateway, iconName))}
          </div>

          <label class="field-block field-block-full">
            <span class="field-label">Description</span>
            <textarea
              class="field-control field-textarea"
              data-wayfinder-gateway-description
              .value=${gateway.description ?? ''}
              placeholder="Explain what this ${gateway.gatewayType === 'Split' ? 'split' : 'join'} point does and why it exists."
              @change=${this._updateGatewayDescription}
            ></textarea>
          </label>
        </section>

        <section class="inspector-section" aria-labelledby="gateway-routing-heading">
          <h3 id="gateway-routing-heading" class="section-heading">Routing</h3>
          <dl class="meta-list">
            <div class="meta-row">
              <dt>Kind</dt>
              <dd>${isJoin ? 'Join — converges multiple queue paths' : 'Split — branches into multiple queue paths'}</dd>
            </div>
            <div class="meta-row">
              <dt>Related routes</dt>
              <dd>${binding?.relatedTransitionIndices.length ?? 0} transition${(binding?.relatedTransitionIndices.length ?? 0) === 1 ? '' : 's'}</dd>
            </div>
            ${
              binding?.anchorStageKey
                ? html`
                  <div class="meta-row">
                    <dt>${isJoin ? 'Merge near' : 'Branches from'}</dt>
                    <dd>${this._routes.stageLabel(binding.anchorStageKey)}</dd>
                  </div>
                `
                : nothing
            }
          </dl>
          <p class="action-summary gateway-routing-hint">
            Use route editing to bind stages through this gateway so the authored flow stays visible as stage → gateway → stage.
            ${isJoin ? ' Join gateways wait for all required incoming paths before releasing.' : ' Split gateways create independent paths for each outgoing transition.'}
          </p>
        </section>

        ${
          isJoin
            ? html`
              <section class="inspector-section" aria-labelledby="gateway-waiting-heading">
                <div class="section-header-row">
                  <h3 id="gateway-waiting-heading" class="section-heading">Waiting information</h3>
                  <wayfinder-inline-help
                    label="Waiting information help"
                    message="Join gateways own the waiting story for their queue. This message is shown to users in the owning queue while they wait for other queues to arrive. Authors set it here rather than on a separate waiting stage."
                  ></wayfinder-inline-help>
                </div>
                <div class="field-grid">
                  <label class="field-block field-block-full">
                    <span class="field-label">Waiting message</span>
                    <textarea
                      class="field-control field-textarea"
                      data-wayfinder-gateway-waiting-content
                      .value=${gateway.waitingContent ?? ''}
                      placeholder="Explain what users in this queue are waiting for, for example: Your application is under review by the planning team."
                      @change=${this._updateJoinWaitingContent}
                    ></textarea>
                  </label>
                  <label class="field-block">
                    <span class="field-label-row">
                      <span class="field-label">Expected wait (seconds)</span>
                      <wayfinder-inline-help
                        label="Expected wait help"
                        message="An approximate maximum wait in seconds. Used by the runtime to set a progress indicator. Leave blank if the wait is open-ended."
                      ></wayfinder-inline-help>
                    </span>
                    <input
                      type="number"
                      class="field-control"
                      data-wayfinder-gateway-waiting-seconds
                      min="0"
                      .value=${String(gateway.waitingExpectedSeconds ?? '')}
                      placeholder="3600"
                      @change=${this._updateJoinWaitingExpectedSeconds}
                    />
                  </label>
                  <div class="field-block">
                    <span class="field-label">Allow defer</span>
                    <label class="checkbox-row">
                      <input
                        type="checkbox"
                        data-wayfinder-gateway-waiting-allow-defer
                        ?checked=${gateway.waitingAllowDefer ?? false}
                        @change=${this._updateJoinWaitingAllowDefer}
                      />
                      <span>Users in this queue can defer the wait</span>
                    </label>
                  </div>
                  ${
                    gateway.waitingAllowDefer
                      ? html`
                        <label class="field-block">
                          <span class="field-label">Defer message</span>
                          <input
                            class="field-control"
                            data-wayfinder-gateway-waiting-defer-message
                            .value=${gateway.waitingDeferMessage ?? ''}
                            placeholder="You can return to this step when the other team has finished."
                            @change=${this._updateJoinWaitingDeferMessage}
                          />
                        </label>
                      `
                      : nothing
                  }
                </div>
              </section>
            `
            : nothing
        }

        ${this._routes.renderGatewayRoutes(gateway, binding)}

        <section class="inspector-section" aria-labelledby="gateway-danger-heading">
          <h3 id="gateway-danger-heading" class="section-heading">Actions</h3>
          <div class="action-buttons">
            <button
              type="button"
              class="icon-button danger-button"
              data-wayfinder-gateway-delete
              @click=${this._deleteSelectedGateway}
            >
              Delete gateway
            </button>
          </div>
        </section>
      </article>
    `;
  }
}
