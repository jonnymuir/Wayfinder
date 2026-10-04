import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import type { ServiceBlueprint } from './types.js';
import { BlueprintClipboard, type ClipboardContext } from './blueprint-clipboard.js';
import { DefinitionController } from './definition-controller.js';
import { cloneServiceBlueprint, serviceBlueprintsEqual } from './blueprint-snapshot.js';
import { EditorKeyboard } from './editor-keyboard.js';
import { EditHistory } from './edit-history.js';
import { PanelLayoutController } from './panel-layout-controller.js';
import { ShortcutGuideController } from './shortcut-guide-controller.js';
import { SaveController } from './save-controller.js';
import { ToastController } from './toast-controller.js';
import { StalenessController } from './staleness-controller.js';
import { ValidationController } from './validation-controller.js';
import { EditorCatalogsController } from './editor-catalogs-controller.js';
import {
  type EditorSelection,
  SelectionController,
  type SelectionDetail,
  cloneSelection,
  selectionFromDetail,
  selectionsEqual,
} from './editor-selection.js';
import { ValidationNavigator } from './validation-navigation.js';
import { renderEditorToolbar, type ToolbarModel } from './editor-toolbar.js';
import { renderCanvasHealthHint, renderPanelHeader } from './editor-panels.js';
import type { ServiceBlueprintSource } from './service-blueprint-source.js';
import type { ServiceBlueprintActionCatalog } from './action-catalog.js';
import type { ServiceBlueprintComponentCatalog } from './component-catalog.js';
import type { ServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';
import type { ServiceBlueprintAuthorContext } from './service-blueprint-author-context.js';
import type { QueueDefinition } from './stage-assignment.js';
import './wayfinder-service-blueprint-graph.js';
import './wayfinder-step-inspector.js';
import './wayfinder-calculations-editor.js';
import './wayfinder-service-blueprint-outline.js';
import './wayfinder-confidence-tabs.js';
import type { ConfidenceTab } from './wayfinder-confidence-tabs.js';
import editorStyles from './wayfinder-service-blueprint-editor.css?inline';

type ServiceBlueprintHistoryEntry = {
  serviceBlueprint: ServiceBlueprint;
  selection: EditorSelection;
};

/** What the graph, calculations editor and inspector report when they change the blueprint. */
type ServiceBlueprintUpdatedDetail = { serviceBlueprint: ServiceBlueprint; selection?: SelectionDetail };

/**
 * Top-level editor host page composing the four V1 serviceBlueprint editor components.
 *
 * Layout:
 *   Left  — wayfinder-service-blueprint-graph (with title bar + mode toggle)
 *   Right — wayfinder-step-inspector
 *
 * URL param: ?serviceBlueprint=<key>  (default: "planning")
 * Prop: initialServiceBlueprint — set directly for Storybook / offline use; skips API fetch.
 *
 * Test hooks:
 *   data-wayfinder-component="service-blueprint-editor"
 *   data-wayfinder-service-blueprint-loaded="{key}" (reflected on the custom-element host once ready)
 *   data-wayfinder-toast  (on the toast confirmation banner)
 *   data-wayfinder-save-error (on the persistent save error surface)
 */
@customElement('wayfinder-service-blueprint-editor')
export class WayfinderServiceBlueprintEditorElement extends LitElement {
  /** ServiceBlueprint key — read from ?serviceBlueprint= URL param or set directly. No implicit default: a
   * host must supply one (directly, or via the shell's own serviceBlueprint list/auto-select) — there
   * is no single serviceBlueprint name that's a sensible fallback across every possible host. */
  @property({ type: String, attribute: 'blueprint-key' })
  blueprintKey = '';

  /**
   * Host-supplied source the editor reads serviceBlueprints from and writes back to.
   * Required for runtime use; Storybook stories pass `initialServiceBlueprint` instead
   * and can leave this unset.
   */
  @property({ attribute: false })
  serviceBlueprintSource?: ServiceBlueprintSource;

  /**
   * Host-supplied catalog of action types the editor can render. Falls back
   * to Wayfinder's built-in catalog when the host does not extend it.
   */
  @property({ attribute: false })
  actionCatalog?: ServiceBlueprintActionCatalog;

  /**
   * Host-supplied catalog of component types the properties panel's add/edit UI can offer —
   * see docs/guides/extending-the-component-catalog.md. Falls back to a live fetch from
   * whichever host this editor instance is talking to (see HttpServiceBlueprintComponentCatalog),
   * NOT a hand-mirrored static stub like actionCatalog's default — a host-registered custom
   * component type should appear here with no editor code change. If the fetch fails (no live
   * host, e.g. an offline Storybook story with no explicit override), the add/edit UI degrades
   * gracefully to a read-only component list, same as before this feature existed.
   */
  @property({ attribute: false })
  componentCatalog?: ServiceBlueprintComponentCatalog;

  /**
   * Host-supplied catalog of registered support systems a support-system-call action's own
   * editor can offer — see docs/guides/support-systems.md. Same "live fetch, not a hand-mirrored
   * stub" default as componentCatalog above.
   */
  @property({ attribute: false })
  supportSystemCatalog?: ServiceBlueprintSupportSystemCatalog;

  /** Optional UX hint about the current author. Never authoritative. */
  @property({ attribute: false })
  authorContext?: ServiceBlueprintAuthorContext;

  /** Host-supplied queues used for queue labels and authoring pickers. */
  @property({ attribute: false })
  availableQueues: QueueDefinition[] = [];

  /**
   * If set, the component uses this service blueprint directly instead of fetching from
   * the API.  Designed for Storybook stories and offline walkthrough fixtures.
   */
  @property({ attribute: false })
  initialServiceBlueprint: ServiceBlueprint | null = null;

  @state() private _serviceBlueprint: ServiceBlueprint | null = null;
  @state() private _loading = false;
  @state() private _error: string | null = null;
  @state() private _historyAnnouncement = '';
  @state() private _activeConfidenceTab: ConfidenceTab = 'canvas';
  /** Prefixed node ids from the canvas's shift-marquee multi-selection. */
  @state() private _graphMultiSelection: string[] = [];
  /** Relayed from the graph's own zoom-changed event — see graph-panel's hide-own-toolbar. */
  @state() private _graphZoom = 1;
  @query('.graph-panel') private _graphElement?: HTMLElementTagNameMap['wayfinder-service-blueprint-graph'];

  private readonly _toast = new ToastController(this);
  private readonly _history = new EditHistory<ServiceBlueprintHistoryEntry>();
  private readonly _clipboard = new BlueprintClipboard();
  private readonly _layout = new PanelLayoutController(this);
  private readonly _help = new ShortcutGuideController(this);
  private readonly _selection = new SelectionController(this, {
    blueprint: () => this._serviceBlueprint,
    revealInspector: () => this._layout.expandInspector(),
  });
  private readonly _catalogs = new EditorCatalogsController(
    this,
    { actions: () => this.actionCatalog, components: () => this.componentCatalog, supportSystems: () => this.supportSystemCatalog },
    (changed) => {
      // Component schema issues in the Definition tab only re-lint on text edits, so a catalog that
      // arrives after the tab was opened would otherwise leave them unflagged until the next keystroke.
      if (changed === 'components') {
        this._definition.relint();
      }
      this._validation.schedule();
    }
  );
  private readonly _staleness = new StalenessController(this, {
    source: () => this.serviceBlueprintSource,
    blueprintKey: () => this.blueprintKey,
    loadedVersion: () => this._serviceBlueprint?.version ?? null,
    reload: () => this._handleReloadAfterConflict(),
  });
  private readonly _validation = new ValidationController(this, () => ({
    blueprint: this._serviceBlueprint,
    blueprintKey: this.blueprintKey,
    source: this.serviceBlueprintSource,
    actionCatalog: this._catalogs.actions,
    componentCatalog: this._catalogs.components,
    supportSystemCatalog: this._catalogs.supportSystems,
  }));
  private readonly _definition = new DefinitionController(this, {
    blueprint: () => this._serviceBlueprint,
    componentCatalog: () => this._catalogs.components,
    apply: (next) => this._commitServiceBlueprintUpdate(next, this._selection.current),
  });
  private readonly _save = new SaveController(this, {
    blueprint: () => this._serviceBlueprint,
    blueprintKey: () => this.blueprintKey,
    source: () => this.serviceBlueprintSource,
    hasBlockingIssues: () => this._validation.hasBlocking,
    allowedByContext: () => this._canSaveByContext,
    adopt: (saved) => {
      this._serviceBlueprint = saved;
    },
    conflict: (version) => this._staleness.markStale(version),
    toast: (message) => this._toast.show(message),
    jumpToStage: (stageKey) => this._navigator.jumpToStage(stageKey),
  });
  private readonly _navigator = new ValidationNavigator({
    blueprint: () => this._serviceBlueprint,
    selection: this._selection,
    showTab: (tab) => {
      this._activeConfidenceTab = tab;
    },
    revealInspector: () => this._layout.expandInspector(),
    inspector: () => this.shadowRoot?.querySelector<HTMLElement>('wayfinder-step-inspector') ?? null,
  });
  /** Registers itself with the element; nothing else needs to call it. */
  readonly keyboard = new EditorKeyboard(this, {
    save: () => void this._save.save(),
    undo: () => this._undo(),
    redo: () => this._redo(),
    copy: () => this._copySelection(),
    paste: () => this._pasteClipboard(),
    canUndo: () => this._history.canUndo,
    canRedo: () => this._history.canRedo,
    openHelp: () => this._help.open(this.shadowRoot?.activeElement as HTMLElement | null),
    isHelpOpen: () => this._help.isOpen,
  });

  private _lastLoadedBlueprintKey: string | null = null;
  private _serviceBlueprintLoadRequestId = 0;

  connectedCallback() {
    super.connectedCallback();
    this._reflectServiceBlueprintLoadedState();

    // Honour ?serviceBlueprint= URL param when running as a standalone page
    if (typeof window !== 'undefined') {
      const keyParam = new URLSearchParams(window.location.search).get('serviceBlueprint');
      if (keyParam && !this.hasAttribute('blueprint-key')) {
        this.blueprintKey = keyParam;
      }
    }

    if (this.initialServiceBlueprint) {
      this._initialiseEditorState(this.initialServiceBlueprint);
      this._lastLoadedBlueprintKey = this.blueprintKey;
    } else {
      void this._loadServiceBlueprint();
    }
  }

  willUpdate(changedProperties: Map<string, unknown>) {
    // Watch for serviceBlueprint key changes and reload
    if (changedProperties.has('blueprintKey') && this.blueprintKey !== this._lastLoadedBlueprintKey && !this.initialServiceBlueprint) {
      void this._loadServiceBlueprint();
    }
  }

  updated(changedProperties: Map<string, unknown>) {
    this._definition.syncFromBlueprint();

    // Recompute the validation rail when the blueprint or the host source changes (catalog changes
    // schedule it themselves). Debounced: a server `validate` is a round-trip, and the in-browser
    // fallback is cheap enough that debouncing it too keeps the two paths behaving identically. The
    // first load (null → a blueprint) validates immediately so the rail isn't blank right after opening.
    const blueprintChanged = changedProperties.has('_serviceBlueprint');
    if (blueprintChanged && !changedProperties.get('_serviceBlueprint') && this._serviceBlueprint) {
      void this._validation.run();
    } else if (blueprintChanged || changedProperties.has('serviceBlueprintSource')) {
      this._validation.schedule();
    }
  }

  private async _loadServiceBlueprint() {
    const requestId = ++this._serviceBlueprintLoadRequestId;
    this._loading = true;
    this._error = null;
    this._reflectServiceBlueprintLoadedState();
    this._lastLoadedBlueprintKey = this.blueprintKey;

    if (!this.serviceBlueprintSource) {
      // Empty state — no source wired. The shell renders a developer affordance; the editor element
      // itself stays silently empty so Storybook stories that drive it via `initialServiceBlueprint`
      // are not disturbed.
      this._serviceBlueprint = null;
      this._loading = false;
      this._reflectServiceBlueprintLoadedState();
      return;
    }

    try {
      const serviceBlueprint = await this.serviceBlueprintSource.load(this.blueprintKey);
      if (requestId === this._serviceBlueprintLoadRequestId) {
        this._initialiseEditorState(serviceBlueprint);
      }
    } catch (err) {
      if (requestId === this._serviceBlueprintLoadRequestId) {
        this._error = err instanceof Error ? err.message : String(err);
        this._serviceBlueprint = null;
        this._reflectServiceBlueprintLoadedState();
      }
    } finally {
      if (requestId === this._serviceBlueprintLoadRequestId) {
        this._loading = false;
      }
    }
  }

  private _initialiseEditorState(serviceBlueprint: ServiceBlueprint) {
    this._serviceBlueprint = cloneServiceBlueprint(serviceBlueprint);
    this._reflectServiceBlueprintLoadedState();
    this._history.clear();
    this._selection.clearAction();
    this._save.loaded(this._serviceBlueprint);
    this._definition.reset();
    this._selection.apply(null, this._serviceBlueprint);
    this._announceHistory('Service blueprint loaded. Undo history is ready for your next edit.');
    this._staleness.reset();
  }

  private _reflectServiceBlueprintLoadedState() {
    const loadedKey = this.blueprintKey?.trim() || this._serviceBlueprint?.definitionKey?.trim();
    if (loadedKey) {
      this.setAttribute('data-wayfinder-service-blueprint-loaded', loadedKey);
      return;
    }

    this.removeAttribute('data-wayfinder-service-blueprint-loaded');
  }

  private _snapshotCurrentState(): ServiceBlueprintHistoryEntry | null {
    if (!this._serviceBlueprint) {
      return null;
    }

    return {
      serviceBlueprint: cloneServiceBlueprint(this._serviceBlueprint),
      selection: cloneSelection(this._selection.current),
    };
  }

  private _restoreHistoryEntry(entry: ServiceBlueprintHistoryEntry) {
    this._serviceBlueprint = cloneServiceBlueprint(entry.serviceBlueprint);
    this._selection.apply(cloneSelection(entry.selection), this._serviceBlueprint);
    this._selection.clearAction();
  }

  private _announceHistory(message: string) {
    this._historyAnnouncement = '';
    requestAnimationFrame(() => {
      this._historyAnnouncement = message;
    });
  }

  private get _historyStatusSummary() {
    return this._serviceBlueprint ? this._history.summary : 'History unavailable until the service blueprint loads.';
  }

  /** Public hook for tests/host: run the pending revalidation now instead of after the debounce. */
  async flushValidationPending() {
    await this._validation.flush();
    await this.updateComplete;
  }

  // Public hook for tests/host: flush debounce and apply if valid.
  applyDefinitionPending() {
    this._definition.flush();
  }

  private _commitServiceBlueprintUpdate(nextServiceBlueprint: ServiceBlueprint, nextSelection: EditorSelection) {
    const selectionChanged = !selectionsEqual(this._selection.current, nextSelection);

    if (serviceBlueprintsEqual(this._serviceBlueprint, nextServiceBlueprint)) {
      if (selectionChanged) {
        this._selection.apply(nextSelection, nextServiceBlueprint);
        this._selection.clearAction();
      }
      return;
    }

    const currentState = this._snapshotCurrentState();
    if (currentState) {
      this._history.record(currentState);
    }

    if (selectionChanged) {
      this._selection.clearAction();
    }

    this._serviceBlueprint = nextServiceBlueprint;
    this._save.edited();
    this._selection.apply(nextSelection, nextServiceBlueprint);
    this._announceHistory(`Change recorded. ${this._historyStatusSummary}`);
  }

  private _undo = () => {
    const current = this._snapshotCurrentState();
    const previous = current && this._history.undo(current);
    if (!previous) {
      return;
    }

    this._restoreHistoryEntry(previous);
    this._announceHistory(`Undid the last serviceBlueprint change. ${this._historyStatusSummary}`);
  };

  private _redo = () => {
    const current = this._snapshotCurrentState();
    const next = current && this._history.redo(current);
    if (!next) {
      return;
    }

    this._restoreHistoryEntry(next);
    this._announceHistory(`Redid the service blueprint change. ${this._historyStatusSummary}`);
  };

  // ---------------------------------------------------------------------------
  // Selection events from the graph, outline and inspector
  // ---------------------------------------------------------------------------

  private _select(selection: EditorSelection) {
    this._selection.apply(selection);
    this._selection.clearAction();
  }

  private _handleStageSelected(e: CustomEvent<{ stageKey: string }>) {
    this._select({ kind: 'stage', stageKey: e.detail.stageKey });
  }

  private _handleGatewaySelected(e: CustomEvent<{ gatewayKey: string }>) {
    this._select({ kind: 'gateway', gatewayKey: e.detail.gatewayKey });
  }

  private _handleTransitionSelected(e: CustomEvent<{ transitionIndex: number }>) {
    this._selection.highlightTransition(e.detail.transitionIndex);
    this._selection.clearAction();
  }

  private _handleOutlineGatewaySelected(e: CustomEvent<{ gatewayKey: string }>) {
    this._handleGatewaySelected(e);
    const gateway = this._serviceBlueprint?.gateways?.find((candidate) => candidate.key === e.detail.gatewayKey);
    if (gateway) {
      this._announceHistory(`Selected gateway ${gateway.displayName}`);
    }
  }

  private _handleActionSelected(e: CustomEvent<{ index: number | null; target: 'stage' | 'transition' }>) {
    this._selection.chooseAction(e.detail.index === null ? null : { target: e.detail.target, index: e.detail.index });
  }

  private _handleServiceBlueprintUpdated(e: CustomEvent<ServiceBlueprintUpdatedDetail>) {
    const nextServiceBlueprint = cloneServiceBlueprint(e.detail.serviceBlueprint);
    const detail = e.detail.selection;
    // A route (e.g. the one just created by drag-to-connect) is not an EditorSelection: it is
    // highlighted separately, which also selects its owner. Handle it before the selection is
    // normalised away to null and leaves the properties panel empty.
    if (detail?.kind === 'transition' && typeof detail.transitionIndex === 'number') {
      this._commitServiceBlueprintUpdate(nextServiceBlueprint, null);
      this._selection.highlightTransition(detail.transitionIndex, nextServiceBlueprint);
      return;
    }
    this._commitServiceBlueprintUpdate(nextServiceBlueprint, selectionFromDetail(detail));
  }

  private _handleInspectorRequested() {
    this._layout.expandInspector();
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLElement>('wayfinder-step-inspector')?.focus();
    });
  }

  private _handleConfidenceTabChanged = (e: CustomEvent<{ tab: ConfidenceTab }>) => {
    this._activeConfidenceTab = e.detail.tab;
    if (e.detail.tab === 'definition') {
      void this._definition.ensureEditorLoaded();
    }
  };

  private async _handleReloadAfterConflict() {
    this._save.clearOutcome();
    // Deliberately NOT clearing the stale flag here — it must stay true (read-only overlay up,
    // banner's Reload button available) until _loadServiceBlueprint actually succeeds.
    // _initialiseEditorState clears it on success. If the reload itself fails, we're correctly still
    // stale/read-only rather than briefly unlocked with old content.
    await this._loadServiceBlueprint();
    if (!this._staleness.stale) {
      this._toast.show('Reloaded the latest version.');
    }
  }

  // ---------------------------------------------------------------------------
  // Copy and paste
  // ---------------------------------------------------------------------------

  private get _clipboardContext(): ClipboardContext {
    return {
      blueprint: this._serviceBlueprint,
      selectedStageKey: this._selection.stageKey,
      multiSelection: this._graphMultiSelection,
      selectedAction: this._selection.currentAction(),
      actionCatalog: this._catalogs.actions,
      availableQueues: this.availableQueues,
    };
  }

  private _copySelection() {
    const message = this._clipboard.copy(this._clipboardContext);
    if (!message) {
      return false;
    }
    this._toast.show(message);
    this.requestUpdate();
    return true;
  }

  private _pasteClipboard() {
    const outcome = this._clipboard.paste(this._clipboardContext);
    if (!outcome.ok) {
      if (outcome.message) {
        this._toast.show(outcome.message);
      }
      return false;
    }

    const selection: EditorSelection = outcome.selectStageKey
      ? { kind: 'stage', stageKey: outcome.selectStageKey }
      : this._selection.current;
    this._commitServiceBlueprintUpdate(outcome.blueprint, selection);
    if (outcome.selectActionIndex !== undefined) {
      this._selection.chooseAction({ target: 'stage', index: outcome.selectActionIndex });
    }
    this._toast.show(outcome.message);
    if (outcome.revealInspector) {
      this._handleInspectorRequested();
    }
    return true;
  }

  private get _canSaveByContext(): boolean {
    return this.authorContext?.canSave !== false;
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  private _toolbarModel(): ToolbarModel {
    const graph = () => this._graphElement;
    return {
      title: this._serviceBlueprint?.displayName ?? 'Service Blueprint Editor',
      save: {
        saving: this._save.isSaving,
        enabled: this._save.canSave,
        allowedByContext: this._canSaveByContext,
        dirtySummary: this._save.dirtySummary,
        onSave: () => void this._save.save(),
      },
      history: { canUndo: this._history.canUndo, canRedo: this._history.canRedo, onUndo: this._undo, onRedo: this._redo },
      canvas:
        this._activeConfidenceTab === 'canvas'
          ? {
              clipboard: {
                canCopy: this._clipboard.canCopy(this._clipboardContext),
                canPaste: this._clipboard.canPaste(this._clipboardContext),
                pasteTitle: this._clipboard.summary,
                onCopy: () => this._copySelection(),
                onPaste: () => this._pasteClipboard(),
              },
              onHelp: (trigger) => this._help.open(trigger),
              graph: {
                addStage: (trigger) => graph()?.addStage(trigger),
                addGateway: (trigger) => graph()?.addGateway(trigger),
                tidyLayout: () => graph()?.tidyLayout(),
                zoomOut: () => graph()?.zoomOut(),
                zoomIn: () => graph()?.zoomIn(),
                fitToScreen: () => graph()?.fitToScreen(),
                fitWidth: () => graph()?.fitToWidth(),
                zoomPercent: Math.round(this._graphZoom * 100),
              },
            }
          : null,
    };
  }

  private _renderOutlinePanel() {
    const stages = this._serviceBlueprint?.stages.length ?? 0;
    const gateways = this._serviceBlueprint?.gateways?.length ?? 0;
    const collapsed = this._layout.outlineCollapsed;
    return html`
      <section class=${`editor-outline-shell ${collapsed ? 'panel-collapsed' : ''}`}>
        ${renderPanelHeader({
          title: 'Outline',
          subtitle: `${stages} ${stages === 1 ? 'stage' : 'stages'}${gateways ? ` · ${gateways} gateways` : ''}`,
          collapsed,
          toggleHook: 'wayfinder-outline-toggle',
          controls: 'service-blueprint-editor-outline-panel',
          expandLabel: 'Expand outline panel',
          collapseLabel: 'Collapse outline panel',
          opensTowards: 'right',
          onToggle: this._layout.toggleOutline,
        })}
        <div id="service-blueprint-editor-outline-panel" class="panel-body" ?hidden=${collapsed}>
          <wayfinder-service-blueprint-outline
            class="editor-outline"
            data-wayfinder-service-blueprint-outline
            .serviceBlueprint=${this._serviceBlueprint}
            .availableQueues=${this.availableQueues}
            .selectedStageKey=${this._selection.stageKey}
            .selectedGatewayKey=${this._selection.gatewayKey}
            .selectedTransitionIndex=${this._selection.transitionIndex}
            .showHeader=${false}
            @outline-stage-selected=${this._handleStageSelected}
            @outline-gateway-selected=${this._handleOutlineGatewaySelected}
            @outline-transition-selected=${this._handleTransitionSelected}
          ></wayfinder-service-blueprint-outline>
        </div>
      </section>
    `;
  }

  private _renderGraphPanel() {
    return html`
      <div class="editor-center">
        ${renderCanvasHealthHint(this._validation.blocking.length, this._validation.warnings.length, () => {
          this._activeConfidenceTab = 'validation';
        })}
        <div class="sr-only" role="status" aria-live="polite" data-wayfinder-history-status>${this._historyAnnouncement}</div>

        <wayfinder-service-blueprint-graph
          class="graph-panel"
          .serviceBlueprint=${this._serviceBlueprint}
          .availableQueues=${this.availableQueues}
          .selectedStageKey=${this._selection.stageKey}
          .selectedGatewayKey=${this._selection.gatewayKey}
          .selectedTransitionIndex=${this._selection.transitionIndex}
          .hideOwnToolbar=${true}
          @stage-selected="${this._handleStageSelected}"
          @gateway-selected="${this._handleGatewaySelected}"
          @transition-selected="${this._handleTransitionSelected}"
          @service-blueprint-updated="${this._handleServiceBlueprintUpdated}"
          @inspector-requested="${this._handleInspectorRequested}"
          @zoom-changed="${(event: CustomEvent<{ zoom: number }>) => {
            this._graphZoom = event.detail.zoom;
          }}"
          @graph-multi-selection="${(event: CustomEvent<{ nodeIds: string[] }>) => {
            this._graphMultiSelection = event.detail.nodeIds;
          }}"
        ></wayfinder-service-blueprint-graph>
      </div>
    `;
  }

  private _renderInspectorPanel() {
    const collapsed = this._layout.inspectorCollapsed;
    return html`
      <section class=${`editor-right ${collapsed ? 'panel-collapsed' : ''}`}>
        ${
          collapsed
            ? nothing
            : html`
              <div
                class="panel-resize-handle"
                role="separator"
                aria-orientation="vertical"
                aria-label="Resize properties panel"
                aria-valuenow=${this._layout.inspectorWidth}
                aria-valuemin="280"
                aria-valuemax="720"
                tabindex="0"
                @pointerdown=${this._layout.startResize}
                @keydown=${this._layout.resizeWithKeyboard}
              ></div>
            `
        }
        ${renderPanelHeader({
          title: 'Properties',
          subtitle: 'Selected stage, gateway, or route details',
          collapsed,
          toggleHook: 'wayfinder-inspector-toggle',
          controls: 'service-blueprint-editor-inspector-panel',
          expandLabel: 'Expand properties drawer',
          collapseLabel: 'Collapse properties drawer',
          opensTowards: 'left',
          onToggle: this._layout.toggleInspector,
        })}
        <div id="service-blueprint-editor-inspector-panel" class="panel-body" ?hidden=${collapsed}>
          <wayfinder-step-inspector
            class="inspector-panel"
            tabindex="0"
            .serviceBlueprint=${this._serviceBlueprint}
            .availableQueues=${this.availableQueues}
            selected-stage-key="${this._selection.stageKey ?? ''}"
            selected-gateway-key="${this._selection.gatewayKey ?? ''}"
            .selectedActionIndex=${this._selection.stageActionIndex}
            .selectedActionTransitionIndex=${this._selection.transitionIndex}
            .actionCatalog=${this._catalogs.actions}
            .componentCatalog=${this._catalogs.components}
            .supportSystemCatalog=${this._catalogs.supportSystems}
            @service-blueprint-updated=${this._handleServiceBlueprintUpdated}
            @action-selected=${this._handleActionSelected}
          ></wayfinder-step-inspector>
        </div>
      </section>
    `;
  }

  render() {
    return html`
      <div
        data-wayfinder-component="service-blueprint-editor"
        data-wayfinder-service-blueprint-loaded="${this.blueprintKey || this._serviceBlueprint?.definitionKey || ''}"
        class="editor-root"
      >
        ${this._toast.render()}
        ${this._loading ? html`<div class="loading-banner" role="status">Loading serviceBlueprint…</div>` : nothing}
        ${this._error ? html`<div class="error-banner" role="alert">${this._error}</div>` : nothing}
        ${this._save.renderError()}
        ${this._staleness.renderBanner()}
        ${renderEditorToolbar(this._toolbarModel())}

        <div class="editor-content-wrapper">
        ${this._staleness.renderOverlay()}
        <wayfinder-confidence-tabs
          class="editor-tabs"
          active-tab="${this._activeConfidenceTab}"
          error-count="${this._validation.blocking.length}"
          warning-count="${this._validation.warnings.length}"
          @tab-changed=${this._handleConfidenceTabChanged}
        >
          <div slot="canvas" class="canvas-workspace">
            <div class=${`editor-shell ${this._layout.resizing ? 'editor-shell-resizing' : ''}`} style=${this._layout.shellStyle}>
              ${this._renderOutlinePanel()}
              ${this._renderGraphPanel()}
              ${this._renderInspectorPanel()}
            </div>
          </div>

          <div slot="calculations">
            <wayfinder-calculations-editor
              .serviceBlueprint=${this._serviceBlueprint}
              .componentCatalog=${this._catalogs.components}
              @service-blueprint-updated=${this._handleServiceBlueprintUpdated}
            ></wayfinder-calculations-editor>
          </div>
          <div slot="validation">${this._validation.renderPanel({ saveStatus: this._save.statusSummary, onJump: (issue) => this._navigator.jump(issue) })}</div>
          <div slot="definition">${this._definition.renderPanel()}</div>
        </wayfinder-confidence-tabs>
        </div>

        ${this._help.render()}
      </div>
    `;
  }

  static styles = unsafeCSS(editorStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-service-blueprint-editor': WayfinderServiceBlueprintEditorElement;
  }
}
