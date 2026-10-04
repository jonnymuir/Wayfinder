import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import type { ActionCatalogEntry, ActionDefinition, ServiceBlueprint, ComponentDescriptor, SupportSystemDescriptor } from './types.js';
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
import type { ServiceBlueprintSource } from './service-blueprint-source.js';
import type { ServiceBlueprintActionCatalog } from './action-catalog.js';
import { BuiltInServiceBlueprintActionCatalog } from './action-catalog.js';
import type { ServiceBlueprintComponentCatalog } from './component-catalog.js';
import { HttpServiceBlueprintComponentCatalog } from './component-catalog.js';
import type { ServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';
import { HttpServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';
import type { ServiceBlueprintAuthorContext } from './service-blueprint-author-context.js';
import type { QueueDefinition } from './stage-assignment.js';
import type { ServiceBlueprintValidationIssue } from './service-blueprint-validation.js';
import { flattenRoutes } from './route-model.js';
import './wayfinder-service-blueprint-graph.js';
import './wayfinder-step-inspector.js';
import './wayfinder-calculations-editor.js';
import './wayfinder-service-blueprint-outline.js';
import './wayfinder-confidence-tabs.js';
import type { ConfidenceTab } from './wayfinder-confidence-tabs.js';
import { renderToolbarIcon } from './graph/toolbar-icons.js';
import editorStyles from './wayfinder-service-blueprint-editor.css?inline';
import { COPY_SHORTCUT, HELP_SHORTCUT, PASTE_SHORTCUT, REDO_SHORTCUT, SAVE_SHORTCUT, UNDO_SHORTCUT } from './editor-shortcut-bindings.js';

type ServiceBlueprintSelection = { kind: 'stage'; stageKey: string } | { kind: 'gateway'; gatewayKey: string } | null;

type ServiceBlueprintHistoryEntry = {
  serviceBlueprint: ServiceBlueprint;
  selection: ServiceBlueprintSelection;
};

type ActionSelection = {
  target: 'stage' | 'transition';
  index: number;
} | null;

function cloneSelection(selection: ServiceBlueprintSelection): ServiceBlueprintSelection {
  return selection ? { ...selection } : null;
}

function selectionsEqual(left: ServiceBlueprintSelection, right: ServiceBlueprintSelection): boolean {
  if (left?.kind !== right?.kind) {
    return false;
  }

  if (left?.kind === 'stage' && right?.kind === 'stage') {
    return left.stageKey === right.stageKey;
  }

  if (left?.kind === 'gateway' && right?.kind === 'gateway') {
    return left.gatewayKey === right.gatewayKey;
  }

  return left === right;
}

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
  @state() private _selection: ServiceBlueprintSelection = null;
  @state() private _selectedTransitionIndex: number | null = null;
  private readonly _toast = new ToastController(this);
  private readonly _staleness = new StalenessController(this, {
    source: () => this.serviceBlueprintSource,
    blueprintKey: () => this.blueprintKey,
    loadedVersion: () => this._serviceBlueprint?.version ?? null,
    reload: () => this._handleReloadAfterConflict(),
  });
  @state() private _loading = false;
  @state() private _error: string | null = null;
  @state() private _actionCatalog: ActionCatalogEntry[] = [];
  @state() private _componentCatalog: ComponentDescriptor[] = [];
  @state() private _supportSystemCatalog: SupportSystemDescriptor[] = [];
  private readonly _history = new EditHistory<ServiceBlueprintHistoryEntry>();
  @state() private _historyAnnouncement = '';
  @state() private _actionSelection: ActionSelection = null;
  private readonly _clipboard = new BlueprintClipboard();

  /** Prefixed node ids from the canvas's shift-marquee multi-selection. */
  @state() private _graphMultiSelection: string[] = [];
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
    jumpToStage: (stageKey) => this._jumpToStage(stageKey),
  });
  private readonly _help = new ShortcutGuideController(this);
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
  @state() private _activeConfidenceTab: ConfidenceTab = 'canvas';
  private readonly _layout = new PanelLayoutController(this);
  /** Relayed from the graph's own zoom-changed event — see graph-panel's hide-own-toolbar. */
  @state() private _graphZoom = 1;
  @query('.graph-panel') private _graphElement?: HTMLElementTagNameMap['wayfinder-service-blueprint-graph'];
  private readonly _definition = new DefinitionController(this, {
    blueprint: () => this._serviceBlueprint,
    componentCatalog: () => this._componentCatalog,
    apply: (next) => this._commitServiceBlueprintUpdate(next, this._currentSelection()),
  });

  private _lastLoadedBlueprintKey: string | null = null;
  private _serviceBlueprintLoadRequestId = 0;

  private readonly _validation = new ValidationController(this, () => ({
    blueprint: this._serviceBlueprint,
    blueprintKey: this.blueprintKey,
    source: this.serviceBlueprintSource,
    actionCatalog: this._actionCatalog,
    componentCatalog: this._componentCatalog,
    supportSystemCatalog: this._supportSystemCatalog,
  }));

  private get _selectedStageKey(): string | null {
    return this._selection?.kind === 'stage' ? this._selection.stageKey : null;
  }

  private get _selectedGatewayKey(): string | null {
    return this._selection?.kind === 'gateway' ? this._selection.gatewayKey : null;
  }

  connectedCallback() {
    super.connectedCallback();
    this._reflectServiceBlueprintLoadedState();

    // Honour ?serviceBlueprint= URL param when running as a standalone page
    if (typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      const keyParam = params.get('serviceBlueprint');
      if (keyParam && !this.hasAttribute('blueprint-key')) {
        this.blueprintKey = keyParam;
      }
    }

    void this._loadActionCatalog();
    void this._loadComponentCatalog();
    void this._loadSupportSystemCatalog();

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

  updated(_changedProperties: Map<string, unknown>) {
    this._definition.syncFromBlueprint();
    // The component catalog fetch (component-catalog.ts) resolves asynchronously, independent
    // of the Definition tab's own debounced re-lint (which only re-runs on text edits) — a user
    // who opens the Definition tab before it resolves would otherwise see component-schema
    // issues only after their next keystroke. Re-lint the already-loaded text once the catalog
    // actually arrives, so it isn't silently skipped for however long that race happens to last.
    if (_changedProperties.has('_componentCatalog')) {
      this._definition.relint();
    }

    // Recompute the validation rail whenever the blueprint, a catalog the fallback validator
    // needs, or the host source itself changes. Debounced inside _scheduleRevalidate — a server
    // `validate` is a round-trip, and the fallback is cheap enough that debouncing it too keeps
    // the two paths behaving identically. The first load (null → a blueprint) validates
    // immediately so the rail isn't blank for the debounce interval right after opening.
    if (
      _changedProperties.has('_serviceBlueprint') ||
      _changedProperties.has('_componentCatalog') ||
      _changedProperties.has('_actionCatalog') ||
      _changedProperties.has('_supportSystemCatalog') ||
      _changedProperties.has('serviceBlueprintSource')
    ) {
      if (_changedProperties.has('_serviceBlueprint') && !_changedProperties.get('_serviceBlueprint') && this._serviceBlueprint) {
        void this._validation.run();
      } else {
        this._validation.schedule();
      }
    }
  }

  disconnectedCallback() {
    super.disconnectedCallback();
  }

  private async _loadServiceBlueprint() {
    const requestId = ++this._serviceBlueprintLoadRequestId;
    this._loading = true;
    this._error = null;
    this._reflectServiceBlueprintLoadedState();
    this._lastLoadedBlueprintKey = this.blueprintKey;

    if (!this.serviceBlueprintSource) {
      // Empty state — no source wired. The shell renders a developer
      // affordance; the editor element itself stays silently empty so
      // Storybook stories that drive it via `initialServiceBlueprint` are not
      // disturbed.
      this._serviceBlueprint = null;
      this._loading = false;
      this._reflectServiceBlueprintLoadedState();
      return;
    }

    try {
      const serviceBlueprint = await this.serviceBlueprintSource.load(this.blueprintKey);
      if (requestId !== this._serviceBlueprintLoadRequestId) {
        return;
      }
      this._initialiseEditorState(serviceBlueprint);
    } catch (err) {
      if (requestId !== this._serviceBlueprintLoadRequestId) {
        return;
      }
      this._error = err instanceof Error ? err.message : String(err);
      this._serviceBlueprint = null;
      this._reflectServiceBlueprintLoadedState();
    } finally {
      if (requestId === this._serviceBlueprintLoadRequestId) {
        this._loading = false;
      }
    }
  }

  private async _loadActionCatalog() {
    const catalog = this.actionCatalog ?? new BuiltInServiceBlueprintActionCatalog();
    this._actionCatalog = await catalog.entries();
  }

  private async _loadComponentCatalog() {
    const catalog = this.componentCatalog ?? new HttpServiceBlueprintComponentCatalog();
    try {
      this._componentCatalog = await catalog.entries();
    } catch {
      // No live host to fetch from (an offline demo, a Storybook story with no override) — the
      // properties panel's add/edit UI simply stays unavailable, same as before this feature
      // existed; never block the rest of the editor on this.
      this._componentCatalog = [];
    }
  }

  private async _loadSupportSystemCatalog() {
    const catalog = this.supportSystemCatalog ?? new HttpServiceBlueprintSupportSystemCatalog();
    try {
      this._supportSystemCatalog = await catalog.entries();
    } catch {
      // Same reasoning as _loadComponentCatalog above — a support-system-call action's own
      // editor simply has nothing to offer in its pickers; never block the rest of the editor.
      this._supportSystemCatalog = [];
    }
  }

  private _initialiseEditorState(serviceBlueprint: ServiceBlueprint) {
    this._serviceBlueprint = cloneServiceBlueprint(serviceBlueprint);
    this._reflectServiceBlueprintLoadedState();
    this._history.clear();
    this._actionSelection = null;
    this._save.loaded(this._serviceBlueprint);
    this._definition.reset();
    this._applySelection(null, this._serviceBlueprint);
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

  private _currentSelection(): ServiceBlueprintSelection {
    return this._selection;
  }

  private _normaliseSelection(
    selection?: { kind: 'stage' | 'gateway' | 'transition'; stageKey?: string; gatewayKey?: string; transitionIndex?: number } | null
  ): ServiceBlueprintSelection {
    if (selection?.kind === 'stage' && selection.stageKey) {
      return { kind: 'stage', stageKey: selection.stageKey };
    }

    if (selection?.kind === 'gateway' && selection.gatewayKey) {
      return { kind: 'gateway', gatewayKey: selection.gatewayKey };
    }

    return null;
  }

  private _applySelection(selection: ServiceBlueprintSelection, serviceBlueprint: ServiceBlueprint | null = this._serviceBlueprint) {
    if (!serviceBlueprint) {
      this._selection = null;
      this._selectedTransitionIndex = null;
      return;
    }

    if (selection?.kind === 'stage') {
      const exists = serviceBlueprint.stages.some((stage) => stage.stageKey === selection.stageKey);
      this._selection = exists ? { kind: 'stage', stageKey: selection.stageKey } : null;
      this._selectedTransitionIndex = null;
      if (this._selection) {
        this._layout.expandInspector();
      }
      return;
    }

    if (selection?.kind === 'gateway') {
      const exists = serviceBlueprint.gateways?.some((gateway) => gateway.key === selection.gatewayKey) ?? false;
      this._selection = exists ? { kind: 'gateway', gatewayKey: selection.gatewayKey } : null;
      this._selectedTransitionIndex = null;
      if (this._selection) {
        this._layout.expandInspector();
      }
      return;
    }

    this._selection = null;
    this._selectedTransitionIndex = null;
  }

  private _applyTransitionHighlight(transitionIndex: number, serviceBlueprint: ServiceBlueprint | null = this._serviceBlueprint) {
    const transitions = flattenRoutes(serviceBlueprint);
    if (!serviceBlueprint || transitionIndex < 0 || transitionIndex >= transitions.length) {
      this._selectedTransitionIndex = null;
      return;
    }
    // wayfinder-step-inspector has no standalone "route" view — a transition is
    // only ever shown nested inside the stage or gateway whose routes[]
    // array actually owns it (mapRouteView sets fromGateway when the owner
    // is a gateway; fromStage always holds the owner's key either way).
    // Without also selecting that owner, the inspector falls through to its
    // empty state and a newly-connected or outline-clicked route never
    // becomes editable.
    const route = transitions[transitionIndex];
    this._selection = route.fromGateway ? { kind: 'gateway', gatewayKey: route.fromGateway } : { kind: 'stage', stageKey: route.fromStage };
    this._selectedTransitionIndex = transitionIndex;
    if (this._selection) {
      this._layout.expandInspector();
    }
  }

  private _snapshotCurrentState(): ServiceBlueprintHistoryEntry | null {
    if (!this._serviceBlueprint) {
      return null;
    }

    return {
      serviceBlueprint: cloneServiceBlueprint(this._serviceBlueprint),
      selection: cloneSelection(this._currentSelection()),
    };
  }

  private _restoreHistoryEntry(entry: ServiceBlueprintHistoryEntry) {
    this._serviceBlueprint = cloneServiceBlueprint(entry.serviceBlueprint);
    this._applySelection(cloneSelection(entry.selection), this._serviceBlueprint);
    this._actionSelection = null;
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

  private get _selectedActionIndex() {
    const currentSelection = this._currentSelection();
    if (!currentSelection || !this._actionSelection) {
      return null;
    }

    return currentSelection.kind === 'stage' && this._actionSelection.target === 'stage' ? this._actionSelection.index : null;
  }

  /** Public hook for tests/host: run the pending revalidation now instead of after the debounce. */
  async flushValidationPending() {
    await this._validation.flush();
    await this.updateComplete;
  }

  private _commitServiceBlueprintUpdate(nextServiceBlueprint: ServiceBlueprint, nextSelection: ServiceBlueprintSelection) {
    const previousSelection = this._currentSelection();

    if (serviceBlueprintsEqual(this._serviceBlueprint, nextServiceBlueprint)) {
      if (!selectionsEqual(previousSelection, nextSelection)) {
        this._applySelection(nextSelection, nextServiceBlueprint);
        this._actionSelection = null;
      }
      return;
    }

    const currentState = this._snapshotCurrentState();
    if (currentState) {
      this._history.record(currentState);
    }

    if (!selectionsEqual(previousSelection, nextSelection)) {
      this._actionSelection = null;
    }

    this._serviceBlueprint = nextServiceBlueprint;
    this._save.edited();
    this._applySelection(nextSelection, nextServiceBlueprint);
    this._announceHistory(`Change recorded. ${this._historyStatusSummary}`);
  }

  private _currentAction(): { action: ActionDefinition; target: 'stage' | 'transition' } | null {
    if (!this._serviceBlueprint || !this._actionSelection) {
      return null;
    }

    if (this._actionSelection.target === 'stage' && this._selectedStageKey) {
      const stage = this._serviceBlueprint.stages.find((candidate) => candidate.stageKey === this._selectedStageKey);
      const action = stage?.actions?.[this._actionSelection.index];
      return action ? { action, target: 'stage' } : null;
    }

    if (this._actionSelection.target === 'transition' && this._selectedTransitionIndex !== null) {
      const transition = flattenRoutes(this._serviceBlueprint)[this._selectedTransitionIndex];
      const action = transition?.actions?.[this._actionSelection.index];
      return action ? { action, target: 'transition' } : null;
    }

    return null;
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
  // Event handlers
  // ---------------------------------------------------------------------------

  private _handleStageSelected(e: CustomEvent<{ stageKey: string }>) {
    this._applySelection({ kind: 'stage', stageKey: e.detail.stageKey }, this._serviceBlueprint);
    this._actionSelection = null;
  }

  private _handleGatewaySelected(e: CustomEvent<{ gatewayKey: string }>) {
    this._applySelection({ kind: 'gateway', gatewayKey: e.detail.gatewayKey }, this._serviceBlueprint);
    this._actionSelection = null;
  }

  private _handleTransitionSelected(e: CustomEvent<{ transitionIndex: number }>) {
    this._applyTransitionHighlight(e.detail.transitionIndex, this._serviceBlueprint);
    this._actionSelection = null;
  }

  private _handleActionSelected(e: CustomEvent<{ index: number | null; target: 'stage' | 'transition' }>) {
    this._actionSelection = e.detail.index === null ? null : { target: e.detail.target, index: e.detail.index };
  }

  private _handleServiceBlueprintUpdated(
    e: CustomEvent<{
      serviceBlueprint: ServiceBlueprint;
      selection?: { kind: 'stage' | 'gateway' | 'transition'; stageKey?: string; gatewayKey?: string; transitionIndex?: number } | null;
    }>
  ) {
    const nextServiceBlueprint = cloneServiceBlueprint(e.detail.serviceBlueprint);
    const detailSelection = e.detail.selection;
    // Transition selections (e.g. the route just created by drag-to-connect)
    // aren't part of ServiceBlueprintSelection — they live in the separate
    // _selectedTransitionIndex field alongside _applyTransitionHighlight.
    // _normaliseSelection has no case for them, so route this before it
    // drops the selection to null and leaves the properties panel empty.
    if (detailSelection?.kind === 'transition' && typeof detailSelection.transitionIndex === 'number') {
      this._commitServiceBlueprintUpdate(nextServiceBlueprint, null);
      this._applyTransitionHighlight(detailSelection.transitionIndex, nextServiceBlueprint);
      return;
    }
    const nextSelection = this._normaliseSelection(detailSelection);
    this._commitServiceBlueprintUpdate(nextServiceBlueprint, nextSelection);
  }

  private _handleInspectorRequested() {
    this._layout.expandInspector();
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLElement>('wayfinder-step-inspector')?.focus();
    });
  }

  private _handleOutlineStageSelected = (e: CustomEvent<{ stageKey: string }>) => {
    this._applySelection({ kind: 'stage', stageKey: e.detail.stageKey }, this._serviceBlueprint);
    this._actionSelection = null;
  };

  private _handleOutlineGatewaySelected = (e: CustomEvent<{ gatewayKey: string }>) => {
    this._applySelection({ kind: 'gateway', gatewayKey: e.detail.gatewayKey }, this._serviceBlueprint);
    this._actionSelection = null;
    const gateway = this._serviceBlueprint?.gateways?.find((g) => g.key === e.detail.gatewayKey);
    if (gateway) {
      this._announceHistory(`Selected gateway ${gateway.displayName}`);
    }
  };

  private _handleOutlineTransitionSelected = (e: CustomEvent<{ transitionIndex: number }>) => {
    this._applyTransitionHighlight(e.detail.transitionIndex, this._serviceBlueprint);
    this._actionSelection = null;
  };

  private _handleConfidenceTabChanged = (e: CustomEvent<{ tab: ConfidenceTab }>) => {
    this._activeConfidenceTab = e.detail.tab;
    if (e.detail.tab === 'definition') {
      void this._definition.ensureEditorLoaded();
    }
  };

  // ---------------------------------------------------------------------------
  // Definition tab — JSON twin-pane sync
  // ---------------------------------------------------------------------------

  // Public hook for tests/host: flush debounce and apply if valid.
  applyDefinitionPending() {
    this._definition.flush();
  }

  private get _clipboardContext(): ClipboardContext {
    return {
      blueprint: this._serviceBlueprint,
      selectedStageKey: this._selectedStageKey,
      multiSelection: this._graphMultiSelection,
      selectedAction: this._currentAction(),
      actionCatalog: this._actionCatalog,
      availableQueues: this.availableQueues,
    };
  }

  private get _canCopy() {
    return this._clipboard.canCopy(this._clipboardContext);
  }

  private get _canPaste() {
    return this._clipboard.canPaste(this._clipboardContext);
  }

  private get _clipboardSummary() {
    return this._clipboard.summary;
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

    const selection = outcome.selectStageKey ? { kind: 'stage' as const, stageKey: outcome.selectStageKey } : this._currentSelection();
    this._commitServiceBlueprintUpdate(outcome.blueprint, selection);
    if (outcome.selectActionIndex !== undefined) {
      this._actionSelection = { target: 'stage', index: outcome.selectActionIndex };
    }
    this._toast.show(outcome.message);
    if (outcome.revealInspector) {
      this._handleInspectorRequested();
    }
    return true;
  }

  private _focusInspectorForValidationIssue(issue: ServiceBlueprintValidationIssue) {
    const actionLocation = issue.location.kind === 'action' ? issue.location : null;
    this._layout.expandInspector();
    requestAnimationFrame(() => {
      const inspector = this.shadowRoot?.querySelector<HTMLElement>('wayfinder-step-inspector');
      inspector?.focus();

      if (!actionLocation) {
        return;
      }

      requestAnimationFrame(() => {
        const actionEditor = inspector?.shadowRoot?.querySelector<HTMLElement>('wayfinder-stage-action-editor');
        const selector =
          actionLocation.fieldKey && actionLocation.fieldKey !== 'fields'
            ? `[data-wayfinder-action-param="${actionLocation.actionIndex}-${actionLocation.fieldKey}"]`
            : typeof actionLocation.formFieldIndex === 'number'
              ? `[data-wayfinder-form-field-key="${actionLocation.actionIndex}-${actionLocation.formFieldIndex}"]`
              : `[data-wayfinder-stage-action="${actionLocation.actionIndex}"]`;
        actionEditor?.shadowRoot?.querySelector<HTMLElement>(selector)?.focus();
      });
    });
  }

  /**
   * Jumps the canvas to a stage named by a save-time diagnostic's path — the server-side
   * counterpart to `_jumpToValidationIssue`'s stage branch, minus the `ServiceBlueprintValidationIssue`
   * object those diagnostics don't have. Selecting the stage is enough to guide someone to the
   * problem; the message itself (already shown in the save-error list) names the specific
   * component and field.
   */
  private _jumpToStage(stageKey: string) {
    if (!this._serviceBlueprint) {
      return;
    }

    this._activeConfidenceTab = 'canvas';
    this._layout.expandInspector();
    this._applySelection({ kind: 'stage', stageKey }, this._serviceBlueprint);
    this._actionSelection = null;
  }

  private _jumpToValidationIssue(issue: ServiceBlueprintValidationIssue) {
    if (!this._serviceBlueprint) {
      return;
    }

    if (issue.location.kind === 'calculation') {
      this._activeConfidenceTab = 'calculations';
      return;
    }

    // A server diagnostic that names nothing navigable — leave the view where it is.
    if (issue.location.kind === 'document') {
      return;
    }

    this._activeConfidenceTab = 'canvas';
    this._layout.expandInspector();

    if (issue.location.kind === 'stage') {
      this._applySelection({ kind: 'stage', stageKey: issue.location.stageKey }, this._serviceBlueprint);
      this._actionSelection = null;
      this._focusInspectorForValidationIssue(issue);
      return;
    }

    if (issue.location.kind === 'route') {
      const gatewayKey = issue.location.routeId;
      const routeId = issue.location.routeId;
      const transitions = flattenRoutes(this._serviceBlueprint);
      const targetIndex = transitions.findIndex((view) => view.key === gatewayKey && view.routeId === routeId);
      if (targetIndex >= 0) {
        this._applyTransitionHighlight(targetIndex, this._serviceBlueprint);
      }
      this._actionSelection = null;
      this._focusInspectorForValidationIssue(issue);
      return;
    }

    if (issue.location.kind === 'action' && issue.location.target === 'route') {
      const gatewayKey = issue.location.routeId;
      const routeId = issue.location.routeId;
      const transitions = flattenRoutes(this._serviceBlueprint);
      const targetIndex = transitions.findIndex((view) => view.key === gatewayKey && view.routeId === routeId);
      this._applyTransitionHighlight(targetIndex >= 0 ? targetIndex : 0, this._serviceBlueprint);
      this._actionSelection = { target: 'transition', index: issue.location.actionIndex };
      this._focusInspectorForValidationIssue(issue);
      return;
    }

    if (issue.location.kind === 'action' && issue.location.target === 'stage') {
      this._applySelection({ kind: 'stage', stageKey: issue.location.stageKey ?? '' }, this._serviceBlueprint);
      this._actionSelection = { target: 'stage', index: issue.location.actionIndex };
      this._focusInspectorForValidationIssue(issue);
    }
  }

  private async _handleReloadAfterConflict() {
    this._save.clearOutcome();
    // Deliberately NOT clearing _serviceBlueprintStale here — that must stay true (read-only
    // overlay up, banner's Reload button available) until _loadServiceBlueprint actually succeeds.
    // _initialiseEditorState clears it on success. If the reload itself fails, we're
    // correctly still stale/read-only rather than briefly unlocked with old content.
    await this._loadServiceBlueprint();
    if (!this._staleness.stale) {
      this._toast.show('Reloaded the latest version.');
    }
  }

  private _renderCalculationsPanel() {
    return html`
      <wayfinder-calculations-editor
        .serviceBlueprint=${this._serviceBlueprint}
        .componentCatalog=${this._componentCatalog}
        @service-blueprint-updated=${this._handleServiceBlueprintUpdated}
      ></wayfinder-calculations-editor>
    `;
  }

  private get _canSaveByContext(): boolean {
    return this.authorContext?.canSave !== false;
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

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

        <!-- Toolbar header: sits above the whole tabbed area (not slotted into any one tab), so
             save/undo/redo — which act on the whole serviceBlueprint, not just the canvas — stay
             visible and usable no matter which tab is active. The rest of this bar (copy/paste,
             add stage/gateway, zoom) only makes sense with the graph on screen, so it's shown
             only while the Canvas tab is active rather than always present and disabled. -->
        <div class="toolbar-header" role="none">
          <h1 id="service-blueprint-editor-title" class="editor-title">
            ${this._serviceBlueprint?.displayName ?? 'Service Blueprint Editor'}
          </h1>
          <div class="toolbar-actions" role="toolbar" aria-label="ServiceBlueprint editor tools">
            <button
              class="toolbar-btn toolbar-btn--icon govuk-button${this._save.isSaving ? ' toolbar-btn--spinning' : ''}"
              data-wayfinder-save
              ?disabled=${!this._save.canSave}
              aria-label=${this._save.isSaving ? 'Saving' : 'Save'}
              title=${
                !this._canSaveByContext
                  ? 'Saving is disabled for the current author.'
                  : `${this._save.dirtySummary} — ${this._save.isSaving ? 'Saving…' : 'Save'}${SAVE_SHORTCUT ? ` (${SAVE_SHORTCUT.labels[0]})` : ''}`
              }
              aria-keyshortcuts=${SAVE_SHORTCUT?.ariaKeys ?? nothing}
              @click=${() => void this._save.save()}
            >
              ${this._save.isSaving ? renderToolbarIcon('saving') : renderToolbarIcon('save')}
            </button>
            <button
              class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
              data-wayfinder-undo
              ?disabled=${!this._history.canUndo}
              aria-label="Undo"
              title=${`Undo${UNDO_SHORTCUT ? ` (${UNDO_SHORTCUT.labels[0]})` : ''}`}
              aria-keyshortcuts=${UNDO_SHORTCUT?.ariaKeys ?? nothing}
              @click=${this._undo}
            >
              ${renderToolbarIcon('undo')}
            </button>
            <button
              class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
              data-wayfinder-redo
              ?disabled=${!this._history.canRedo}
              aria-label="Redo"
              title=${`Redo${REDO_SHORTCUT ? ` (${REDO_SHORTCUT.labels[0]})` : ''}`}
              aria-keyshortcuts=${REDO_SHORTCUT?.ariaKeys ?? nothing}
              @click=${this._redo}
            >
              ${renderToolbarIcon('redo')}
            </button>

            ${
              this._activeConfidenceTab === 'canvas'
                ? html`
                  <span class="toolbar-divider" role="separator" aria-orientation="vertical"></span>
                  <div class="editor-toolbar">
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-copy
                      ?disabled=${!this._canCopy}
                      aria-label="Copy"
                      title=${`Copy${COPY_SHORTCUT ? ` (${COPY_SHORTCUT.labels[0]})` : ''}`}
                      aria-keyshortcuts=${COPY_SHORTCUT?.ariaKeys ?? nothing}
                      @click=${() => this._copySelection()}
                    >
                      ${renderToolbarIcon('copy')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-paste
                      ?disabled=${!this._canPaste}
                      aria-label="Paste"
                      title=${`${this._clipboardSummary}${PASTE_SHORTCUT ? ` (${PASTE_SHORTCUT.labels[0]})` : ''}`}
                      aria-keyshortcuts=${PASTE_SHORTCUT?.ariaKeys ?? nothing}
                      @click=${() => this._pasteClipboard()}
                    >
                      ${renderToolbarIcon('paste')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-help
                      aria-label="Help"
                      title=${`Help${HELP_SHORTCUT ? ` (${HELP_SHORTCUT.labels[0]})` : ''}`}
                      aria-keyshortcuts=${HELP_SHORTCUT?.ariaKeys ?? nothing}
                      @click=${(event: Event) => this._help.open(event.currentTarget as HTMLElement)}
                    >
                      ${renderToolbarIcon('help')}
                    </button>

                    <span class="toolbar-divider" role="separator" aria-orientation="vertical"></span>

                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-add-stage
                      aria-label="Add stage"
                      title="Add stage"
                      @click=${(event: Event) => this._graphElement?.addStage(event.currentTarget as HTMLElement)}
                    >
                      ${renderToolbarIcon('addStage')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-add-gateway
                      aria-label="Add gateway"
                      title="Add gateway"
                      @click=${(event: Event) => this._graphElement?.addGateway(event.currentTarget as HTMLElement)}
                    >
                      ${renderToolbarIcon('addGateway')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-auto-arrange
                      aria-label="Tidy layout"
                      title="Tidy layout"
                      @click=${() => this._graphElement?.tidyLayout()}
                    >
                      ${renderToolbarIcon('tidyLayout')}
                    </button>

                    <span class="toolbar-divider" role="separator" aria-orientation="vertical"></span>

                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      aria-label="Zoom out"
                      title="Zoom out"
                      @click=${() => this._graphElement?.zoomOut()}
                    >
                      ${renderToolbarIcon('zoomOut')}
                    </button>
                    <span class="zoom-indicator" data-wayfinder-zoom>${Math.round(this._graphZoom * 100)}%</span>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      aria-label="Zoom in"
                      title="Zoom in"
                      @click=${() => this._graphElement?.zoomIn()}
                    >
                      ${renderToolbarIcon('zoomIn')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-fit-screen
                      aria-label="Fit to screen"
                      title="Fit to screen"
                      @click=${() => this._graphElement?.fitToScreen()}
                    >
                      ${renderToolbarIcon('fitToScreen')}
                    </button>
                    <button
                      class="toolbar-btn toolbar-btn--icon govuk-button govuk-button--secondary"
                      data-wayfinder-fit-width
                      aria-label="Fit width"
                      title="Fit width"
                      @click=${() => this._graphElement?.fitToWidth()}
                    >
                      ${renderToolbarIcon('fitWidth')}
                    </button>
                  </div>
                `
                : nothing
            }
          </div>
        </div>

        <!-- Tab-based navigation -->
        <div class="editor-content-wrapper">
        ${this._staleness.renderOverlay()}
        <wayfinder-confidence-tabs
          class="editor-tabs"
          active-tab="${this._activeConfidenceTab}"
          error-count="${this._validation.blocking.length}"
          warning-count="${this._validation.warnings.length}"
          @tab-changed=${this._handleConfidenceTabChanged}
        >
          <!-- Canvas tab: main workspace -->
          <div slot="canvas" class="canvas-workspace">
            <div
              class=${`editor-shell ${this._layout.resizing ? 'editor-shell-resizing' : ''}`}
              style=${this._layout.shellStyle}
            >
              <!-- Left: outline -->
              <section class=${`editor-outline-shell ${this._layout.outlineCollapsed ? 'panel-collapsed' : ''}`}>
                <div class="panel-header">
                  <div class="panel-header-copy">
                    <h2 class="panel-title">Outline</h2>
                    ${
                      this._layout.outlineCollapsed
                        ? nothing
                        : html`
                          <p class="panel-subtitle">
                            ${this._serviceBlueprint?.stages.length ?? 0} ${(this._serviceBlueprint?.stages.length ?? 0) === 1 ? 'stage' : 'stages'}
                            ${this._serviceBlueprint?.gateways?.length ? ` · ${this._serviceBlueprint.gateways.length} gateways` : ''}
                          </p>
                        `
                    }
                  </div>
                  <button
                    type="button"
                    class="panel-toggle"
                    data-wayfinder-outline-toggle
                    aria-controls="service-blueprint-editor-outline-panel"
                    aria-expanded=${String(!this._layout.outlineCollapsed)}
                    aria-label=${this._layout.outlineCollapsed ? 'Expand outline panel' : 'Collapse outline panel'}
                    @click=${this._layout.toggleOutline}
                  >
                    ${this._layout.outlineCollapsed ? renderToolbarIcon('chevronRight') : renderToolbarIcon('chevronLeft')}
                    <span class="sr-only">${this._layout.outlineCollapsed ? 'Expand outline' : 'Collapse outline'}</span>
                  </button>
                </div>
                <div
                  id="service-blueprint-editor-outline-panel"
                  class="panel-body"
                  ?hidden=${this._layout.outlineCollapsed}
                >
                  <wayfinder-service-blueprint-outline
                    class="editor-outline"
                    data-wayfinder-service-blueprint-outline
                    .serviceBlueprint=${this._serviceBlueprint}
                    .availableQueues=${this.availableQueues}
                    .selectedStageKey=${this._selectedStageKey}
                    .selectedGatewayKey=${this._selectedGatewayKey}
                    .selectedTransitionIndex=${this._selectedTransitionIndex}
                    .showHeader=${false}
                    @outline-stage-selected=${this._handleOutlineStageSelected}
                    @outline-gateway-selected=${this._handleOutlineGatewaySelected}
                    @outline-transition-selected=${this._handleOutlineTransitionSelected}
                  ></wayfinder-service-blueprint-outline>
                </div>
              </section>

              <!-- Center: graph workspace -->
              <div class="editor-center">
                ${(() => {
                  const errorCount = this._validation.blocking.length;
                  const warningCount = this._validation.warnings.length;
                  const total = errorCount + warningCount;
                  if (total === 0) return nothing;
                  const summary =
                    errorCount > 0 && warningCount > 0
                      ? `${errorCount} error${errorCount === 1 ? '' : 's'} and ${warningCount} warning${warningCount === 1 ? '' : 's'} need attention.`
                      : errorCount > 0
                        ? `${errorCount} validation error${errorCount === 1 ? '' : 's'} need attention.`
                        : `${warningCount} validation warning${warningCount === 1 ? '' : 's'} need attention.`;
                  return html`
                    <div
                      class=${`canvas-health-hint ${errorCount > 0 ? 'is-error' : 'is-warning'}`}
                      data-wayfinder-canvas-health-hint
                      role="status"
                    >
                      <span class="canvas-health-summary">${summary}</span>
                      <button
                        type="button"
                        class="canvas-health-action"
                        data-wayfinder-open-validation
                        @click=${() => {
                          this._activeConfidenceTab = 'validation';
                        }}
                      >Open Validation</button>
                    </div>
                  `;
                })()}
                <div class="sr-only" role="status" aria-live="polite" data-wayfinder-history-status>${this._historyAnnouncement}</div>

                <wayfinder-service-blueprint-graph
                  class="graph-panel"
                  .serviceBlueprint=${this._serviceBlueprint}
                  .availableQueues=${this.availableQueues}
                  .selectedStageKey=${this._selectedStageKey}
                  .selectedGatewayKey=${this._selectedGatewayKey}
                  .selectedTransitionIndex=${this._selectedTransitionIndex}
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

              <!-- Right: inspector -->
              <section class=${`editor-right ${this._layout.inspectorCollapsed ? 'panel-collapsed' : ''}`}>
                ${
                  this._layout.inspectorCollapsed
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
                <div class="panel-header">
                  <div class="panel-header-copy">
                    <h2 class="panel-title">Properties</h2>
                    ${this._layout.inspectorCollapsed ? nothing : html`<p class="panel-subtitle">Selected stage, gateway, or route details</p>`}
                  </div>
                  <button
                    type="button"
                    class="panel-toggle"
                    data-wayfinder-inspector-toggle
                    aria-controls="service-blueprint-editor-inspector-panel"
                    aria-expanded=${String(!this._layout.inspectorCollapsed)}
                    aria-label=${this._layout.inspectorCollapsed ? 'Expand properties drawer' : 'Collapse properties drawer'}
                    @click=${this._layout.toggleInspector}
                  >
                    ${this._layout.inspectorCollapsed ? renderToolbarIcon('chevronLeft') : renderToolbarIcon('chevronRight')}
                    <span class="sr-only">${this._layout.inspectorCollapsed ? 'Expand properties drawer' : 'Collapse properties drawer'}</span>
                  </button>
                </div>
                <div
                  id="service-blueprint-editor-inspector-panel"
                  class="panel-body"
                  ?hidden=${this._layout.inspectorCollapsed}
                >
                  <wayfinder-step-inspector
                    class="inspector-panel"
                    tabindex="0"
                    .serviceBlueprint=${this._serviceBlueprint}
                    .availableQueues=${this.availableQueues}
                    selected-stage-key="${this._selectedStageKey ?? ''}"
                    selected-gateway-key="${this._selectedGatewayKey ?? ''}"
                    .selectedActionIndex=${this._selectedActionIndex}
                    .selectedActionTransitionIndex=${this._selectedTransitionIndex}
                    .actionCatalog=${this._actionCatalog}
                    .componentCatalog=${this._componentCatalog}
                    .supportSystemCatalog=${this._supportSystemCatalog}
                    @service-blueprint-updated=${this._handleServiceBlueprintUpdated}
                    @action-selected=${this._handleActionSelected}
                  ></wayfinder-step-inspector>
                </div>
              </section>
            </div>
          </div>

          <!-- Other tabs -->
          <div slot="calculations">${this._renderCalculationsPanel()}</div>
          <div slot="validation">${this._validation.renderPanel({ saveStatus: this._save.statusSummary, onJump: (issue) => this._jumpToValidationIssue(issue) })}</div>
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
