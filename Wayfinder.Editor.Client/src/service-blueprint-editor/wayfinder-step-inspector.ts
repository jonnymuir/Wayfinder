import { LitElement, html, unsafeCSS } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type {
  ActionCatalogEntry,
  ServiceBlueprintGatewayDefinition,
  StageDefinition,
  ComponentDescriptor,
  ServiceBlueprint,
  SupportSystemDescriptor,
} from './types.js';
import { serviceBlueprintGateways } from './types.js';
import { buildPropertyReferenceContext } from './component-property-references.js';

import type { QueueDefinition } from './stage-assignment.js';
import './wayfinder-stage-action-editor.js';
import './wayfinder-inline-help.js';
import './wayfinder-calculation-expression-editor.js';
import { RouteEditorController } from './inspector/route-editor-controller.js';
import { GatewayInspectorController } from './inspector/gateway-inspector-controller.js';
import { StageInspectorController } from './inspector/stage-inspector-controller.js';
import type { InspectorContext } from './inspector/inspector-host.js';
import stepInspectorStyles from './wayfinder-step-inspector.css?inline';

type GraphSelectionDetail = {
  kind: 'stage' | 'gateway';
  stageKey?: string;
  gatewayKey?: string;
};

type ServiceBlueprintUpdatedDetail = {
  serviceBlueprint: ServiceBlueprint;
  selection?: GraphSelectionDetail | null;
};

/**
 * @internal Composition detail of <wayfinder-service-blueprint-editor>; not part of the public API surface.
 *
 * Shows the selected stage or gateway; the stage, gateway and route editing each live in their own controller.
 */
@customElement('wayfinder-step-inspector')
export class WayfinderStepInspectorElement extends LitElement implements InspectorContext {
  @property({ attribute: false })
  serviceBlueprint: ServiceBlueprint | null = null;

  @property({ type: String, attribute: 'selected-stage-key' })
  selectedStageKey: string | null = null;

  @property({ type: String, attribute: 'selected-gateway-key' })
  selectedGatewayKey: string | null = null;

  @property({ attribute: false })
  actionCatalog: ActionCatalogEntry[] = [];

  /**
   * Component types this properties panel can offer for add/edit — see
   * component-catalog.ts. Empty (the default) means no live host catalog is available; the
   * components section falls back to a read-only list, same as before this feature existed.
   */
  @property({ attribute: false })
  componentCatalog: ComponentDescriptor[] = [];

  /** Registered support systems a support-system-call action's own editor can offer — see support-system-catalog.ts. */
  @property({ attribute: false })
  supportSystemCatalog: SupportSystemDescriptor[] = [];

  @property({ attribute: false })
  availableQueues: QueueDefinition[] = [];

  @property({ type: Number, attribute: false })
  selectedActionIndex: number | null = null;

  @property({ type: Number, attribute: false })
  selectedActionTransitionIndex: number | null = null;

  @state() private _statusMessage: string | null = null;

  private readonly _routes = new RouteEditorController(this);
  private readonly _stage = new StageInspectorController(this, this._routes);
  private readonly _gateway = new GatewayInspectorController(this, this._routes);

  get selectedStage(): StageDefinition | null {
    if (!this.serviceBlueprint || !this.selectedStageKey) {
      return null;
    }

    return this.serviceBlueprint.stages.find((stage) => stage.stageKey === this.selectedStageKey) ?? null;
  }

  get selectedGateway(): ServiceBlueprintGatewayDefinition | null {
    if (!this.serviceBlueprint || !this.selectedGatewayKey) {
      return null;
    }

    return serviceBlueprintGateways(this.serviceBlueprint).find((gateway) => gateway.key === this.selectedGatewayKey) ?? null;
  }

  /** Blueprint-wide captured input fields, for a support-system-call action's own inputs — see wayfinder-stage-action-editor.ts's supportSystemFieldReferences doc comment for why this is blueprint-wide, not stage-scoped. */
  get supportSystemFieldReferences() {
    return buildPropertyReferenceContext(this.serviceBlueprint, undefined, this.componentCatalog).allFields;
  }

  announce(message: string) {
    this._statusMessage = '';
    requestAnimationFrame(() => {
      this._statusMessage = message;
    });
  }

  emitUpdated(serviceBlueprint: ServiceBlueprint, selection?: GraphSelectionDetail | null) {
    this.dispatchEvent(
      new CustomEvent<ServiceBlueprintUpdatedDetail>('service-blueprint-updated', {
        detail: { serviceBlueprint, selection },
        bubbles: true,
        composed: true,
      })
    );
  }

  private _renderEmpty() {
    return html`
      <div class="empty-state" role="status">
        <p>Select a stage, gateway, or route from the workspace to inspect its details.</p>
      </div>
    `;
  }

  render() {
    const gateway = this.selectedGateway;
    const stage = gateway ? null : this.selectedStage;

    return html`
      <div class="step-inspector-root" data-wayfinder-component="step-inspector" tabindex="0">
        <div id="inspector-announcer" class="sr-only" role="status" aria-live="polite" aria-atomic="true">${this._statusMessage ?? ''}</div>
        ${gateway ? this._gateway.renderGateway(gateway) : stage ? this._stage.renderStage(stage) : this._renderEmpty()}
      </div>
    `;
  }

  static styles = unsafeCSS(stepInspectorStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-step-inspector': WayfinderStepInspectorElement;
  }
}
