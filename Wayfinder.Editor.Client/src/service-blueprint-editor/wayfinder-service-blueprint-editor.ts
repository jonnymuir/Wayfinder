import { LitElement, html, nothing, unsafeCSS } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import type { ActionCatalogEntry, ActionDefinition, ServiceBlueprint, ComponentDescriptor, SupportSystemDescriptor } from './types.js';
import { BlueprintClipboard, type ClipboardContext } from './blueprint-clipboard.js';
import { DefinitionController } from './definition-controller.js';
import { EditHistory } from './edit-history.js';
import { StalenessController } from './staleness-controller.js';
import { ValidationController } from './validation-controller.js';
import { hydrateServiceBlueprintDefinition } from './blueprint-hydration.js';
import { ServiceBlueprintSaveError, normaliseServiceBlueprintSaveError, type ServiceBlueprintSource } from './service-blueprint-source.js';
import type { ServiceBlueprintActionCatalog } from './action-catalog.js';
import { BuiltInServiceBlueprintActionCatalog } from './action-catalog.js';
import type { ServiceBlueprintComponentCatalog } from './component-catalog.js';
import { HttpServiceBlueprintComponentCatalog } from './component-catalog.js';
import type { ServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';
import { HttpServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';
import type { ServiceBlueprintAuthorContext } from './service-blueprint-author-context.js';
import type { QueueDefinition } from './stage-assignment.js';
import { type ServiceBlueprintValidationIssue } from './service-blueprint-validation.js';
import { flattenRoutes } from './route-model.js';
import { findServiceBlueprintShortcut, matchesShortcut, SERVICE_BLUEPRINT_SHORTCUT_GROUPS } from './editor-shortcuts.js';
import './wayfinder-service-blueprint-graph.js';
import './wayfinder-step-inspector.js';
import './wayfinder-calculations-editor.js';
import './wayfinder-service-blueprint-outline.js';
import './wayfinder-confidence-tabs.js';
import type { ConfidenceTab } from './wayfinder-confidence-tabs.js';
import { renderToolbarIcon } from './graph/toolbar-icons.js';
import editorStyles from './wayfinder-service-blueprint-editor.css?inline';

type ServiceBlueprintSelection = { kind: 'stage'; stageKey: string } | { kind: 'gateway'; gatewayKey: string } | null;

type ServiceBlueprintHistoryEntry = {
  serviceBlueprint: ServiceBlueprint;
  selection: ServiceBlueprintSelection;
};

type ActionSelection = {
  target: 'stage' | 'transition';
  index: number;
} | null;

type SaveState = 'idle' | 'saving' | 'saved' | 'error';

const SAVE_SHORTCUT = findServiceBlueprintShortcut('save');
const UNDO_SHORTCUT = findServiceBlueprintShortcut('undo');
const REDO_SHORTCUT = findServiceBlueprintShortcut('redo');
const COPY_SHORTCUT = findServiceBlueprintShortcut('copy');
const PASTE_SHORTCUT = findServiceBlueprintShortcut('paste');
const HELP_SHORTCUT = findServiceBlueprintShortcut('help');

function cloneServiceBlueprint(serviceBlueprint: ServiceBlueprint): ServiceBlueprint {
  return hydrateServiceBlueprintDefinition(JSON.parse(JSON.stringify(serviceBlueprint)) as ServiceBlueprint);
}

function cloneSelection(selection: ServiceBlueprintSelection): ServiceBlueprintSelection {
  return selection ? { ...selection } : null;
}

function serviceBlueprintsEqual(left: ServiceBlueprint | null, right: ServiceBlueprint | null): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
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
  @state() private _toastMessage: string | null = null;
  private _toastDismissTimer: number | null = null;
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
  @state() private _saveState: SaveState = 'idle';
  @state() private _saveMessage: string | null = null;
  @state() private _saveError: ServiceBlueprintSaveError | null = null;
  @state() private _saveErrorCopyStatus: string | null = null;
  @state() private _helpOpen = false;
  @state() private _activeConfidenceTab: ConfidenceTab = 'canvas';
  // Both start collapsed — the canvas is the primary surface, and either panel is one click
  // away via its own toggle. The inspector auto-expands the moment something is selected (see
  // _applySelection/_applyTransitionHighlight) since a closed Properties panel right after
  // selecting a stage/gateway would just look broken; the outline has no equivalent trigger,
  // so it stays exactly as the author left it.
  @state() private _outlineCollapsed = true;
  @state() private _inspectorCollapsed = true;
  /** Expanded width of the Properties panel in px — dragged via .panel-resize-handle. */
  @state() private _inspectorWidth = 380;
  @state() private _inspectorResizing = false;
  private _inspectorResizeStartX = 0;
  private _inspectorResizeStartWidth = 0;
  /** Relayed from the graph's own zoom-changed event — see graph-panel's hide-own-toolbar. */
  @state() private _graphZoom = 1;
  @query('.graph-panel') private _graphElement?: HTMLElementTagNameMap['wayfinder-service-blueprint-graph'];
  private readonly _definition = new DefinitionController(this, {
    blueprint: () => this._serviceBlueprint,
    componentCatalog: () => this._componentCatalog,
    apply: (next) => this._commitServiceBlueprintUpdate(next, this._currentSelection()),
  });

  private _savedServiceBlueprintSnapshot: ServiceBlueprint | null = null;
  private _helpReturnTarget: HTMLElement | null = null;
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
    this.addEventListener('keydown', this._handleEditorKeydown, true);
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
    if (_changedProperties.has('_saveError') && this._saveError) {
      this.updateComplete.then(() => {
        this.shadowRoot?.querySelector<HTMLElement>('[data-wayfinder-save-error]')?.focus();
      });
    }
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
    this.removeEventListener('keydown', this._handleEditorKeydown, true);
    if (this._toastDismissTimer !== null && typeof window !== 'undefined') {
      window.clearTimeout(this._toastDismissTimer);
    }
    window.removeEventListener('pointermove', this._handleInspectorResizeMove);
    window.removeEventListener('pointerup', this._handleInspectorResizeEnd);
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
    this._savedServiceBlueprintSnapshot = cloneServiceBlueprint(this._serviceBlueprint);
    this._history.clear();
    this._actionSelection = null;
    this._saveState = 'idle';
    this._saveMessage = null;
    this._saveError = null;
    this._saveErrorCopyStatus = null;
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
      this._expandInspectorForSelection();
      return;
    }

    if (selection?.kind === 'gateway') {
      const exists = serviceBlueprint.gateways?.some((gateway) => gateway.key === selection.gatewayKey) ?? false;
      this._selection = exists ? { kind: 'gateway', gatewayKey: selection.gatewayKey } : null;
      this._selectedTransitionIndex = null;
      this._expandInspectorForSelection();
      return;
    }

    this._selection = null;
    this._selectedTransitionIndex = null;
  }

  /**
   * The Properties panel starts collapsed (see _outlineCollapsed/_inspectorCollapsed's
   * comment) — expand it the moment a selection actually resolves to something real, so
   * selecting a stage/gateway/route doesn't leave its own details panel closed. Never
   * re-collapses on its own; the user's explicit toggle is the only way back.
   */
  private _expandInspectorForSelection() {
    if (this._selection && this._inspectorCollapsed) {
      this._inspectorCollapsed = false;
    }
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
    this._expandInspectorForSelection();
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

  private get _isDirty() {
    return !serviceBlueprintsEqual(this._serviceBlueprint, this._savedServiceBlueprintSnapshot);
  }

  private get _canSave() {
    return Boolean(this._serviceBlueprint) && !this._validation.hasBlocking && this._saveState !== 'saving' && this._canSaveByContext;
  }

  private get _dirtyStateSummary() {
    if (!this._serviceBlueprint) {
      return 'Service blueprint not loaded yet.';
    }

    return this._isDirty ? 'Unsaved changes' : 'All changes saved';
  }

  private get _saveStatusSummary() {
    if (this._saveState === 'saving') {
      return 'Saving serviceBlueprint changes…';
    }

    if (this._saveState === 'saved') {
      return this._saveMessage ?? 'Service blueprint changes saved.';
    }

    if (this._saveState === 'error') {
      return this._saveMessage ?? 'Save failed.';
    }

    if (this._validation.hasBlocking) {
      return 'Save is blocked until the blocking validation errors are fixed.';
    }

    return this._saveMessage ?? 'Save is ready.';
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
    this._saveState = 'idle';
    this._saveMessage = null;
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

  private _isEditableTarget(event: KeyboardEvent) {
    return event
      .composedPath()
      .some(
        (target) =>
          target instanceof HTMLElement &&
          (target instanceof HTMLInputElement ||
            target instanceof HTMLTextAreaElement ||
            target instanceof HTMLSelectElement ||
            target.isContentEditable)
      );
  }

  private _handleEditorKeydown = (event: KeyboardEvent) => {
    if (!event.defaultPrevented && HELP_SHORTCUT && matchesShortcut(event, HELP_SHORTCUT)) {
      event.preventDefault();
      this._openShortcutGuide(this.shadowRoot?.activeElement as HTMLElement | null);
      return;
    }

    if (this._helpOpen || event.defaultPrevented || event.altKey) {
      return;
    }

    if (SAVE_SHORTCUT && matchesShortcut(event, SAVE_SHORTCUT)) {
      event.preventDefault();
      void this._handleSave();
      return;
    }

    if (
      ((COPY_SHORTCUT && matchesShortcut(event, COPY_SHORTCUT)) || (PASTE_SHORTCUT && matchesShortcut(event, PASTE_SHORTCUT))) &&
      this._isEditableTarget(event)
    ) {
      return;
    }

    if (COPY_SHORTCUT && matchesShortcut(event, COPY_SHORTCUT)) {
      if (this._copySelection()) {
        event.preventDefault();
      }
      return;
    }

    if (PASTE_SHORTCUT && matchesShortcut(event, PASTE_SHORTCUT)) {
      if (this._pasteClipboard()) {
        event.preventDefault();
      }
      return;
    }

    if (REDO_SHORTCUT && matchesShortcut(event, REDO_SHORTCUT)) {
      event.preventDefault();
      if (this._history.canRedo) {
        this._redo();
      }
      return;
    }

    if (!UNDO_SHORTCUT || !matchesShortcut(event, UNDO_SHORTCUT)) {
      return;
    }

    event.preventDefault();
    if (this._history.canUndo) {
      this._undo();
    }
  };

  private _openShortcutGuide(activator?: HTMLElement | null) {
    this._helpReturnTarget = activator ?? null;
    this._helpOpen = true;
    requestAnimationFrame(() => {
      this.shadowRoot?.querySelector<HTMLElement>('[data-wayfinder-help-close]')?.focus();
    });
  }

  private _closeShortcutGuide() {
    this._helpOpen = false;
    this._helpReturnTarget?.focus();
    this._helpReturnTarget = null;
  }

  private _handleDialogKeydown(event: KeyboardEvent, onClose: () => void) {
    if (event.key === 'Escape') {
      event.preventDefault();
      onClose();
      return;
    }

    if (event.key !== 'Tab') {
      return;
    }

    const root = event.currentTarget as HTMLElement;
    const focusable = Array.from(
      root.querySelectorAll<HTMLElement>('button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])')
    ).filter((element) => !element.hasAttribute('disabled') && element.tabIndex >= 0);
    if (focusable.length === 0) {
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const activeElement = this.shadowRoot?.activeElement as HTMLElement | null;
    if (event.shiftKey && activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

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
    this._inspectorCollapsed = false;
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
    this._showToast(message);
    this.requestUpdate();
    return true;
  }

  private _pasteClipboard() {
    const outcome = this._clipboard.paste(this._clipboardContext);
    if (!outcome.ok) {
      if (outcome.message) {
        this._showToast(outcome.message);
      }
      return false;
    }

    const selection = outcome.selectStageKey ? { kind: 'stage' as const, stageKey: outcome.selectStageKey } : this._currentSelection();
    this._commitServiceBlueprintUpdate(outcome.blueprint, selection);
    if (outcome.selectActionIndex !== undefined) {
      this._actionSelection = { target: 'stage', index: outcome.selectActionIndex };
    }
    this._showToast(outcome.message);
    if (outcome.revealInspector) {
      this._handleInspectorRequested();
    }
    return true;
  }

  private _showToast(message: string) {
    if (this._toastDismissTimer !== null && typeof window !== 'undefined') {
      window.clearTimeout(this._toastDismissTimer);
    }

    this._toastMessage = message;

    const dismiss = () => {
      this._toastMessage = null;
      this._toastDismissTimer = null;
    };

    this._toastDismissTimer =
      typeof window !== 'undefined' ? window.setTimeout(dismiss, 5000) : (setTimeout(dismiss, 5000) as unknown as number);
  }

  private _focusInspectorForValidationIssue(issue: ServiceBlueprintValidationIssue) {
    const actionLocation = issue.location.kind === 'action' ? issue.location : null;
    this._inspectorCollapsed = false;
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
    this._inspectorCollapsed = false;
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
    this._inspectorCollapsed = false;

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

  private async _handleSave() {
    if (!this._serviceBlueprint) {
      return;
    }

    if (this._validation.hasBlocking) {
      this._saveState = 'error';
      this._saveError = new ServiceBlueprintSaveError({
        title: 'Can’t save this service blueprint yet',
        summary: 'Fix the blocking validation errors first.',
        detailLines: ['Open Validation to review each blocking error before trying again.'],
      });
      this._saveMessage = this._saveError.summary;
      this._saveErrorCopyStatus = null;
      return;
    }

    this._saveState = 'saving';
    this._saveMessage = null;
    this._saveErrorCopyStatus = null;

    if (!this.serviceBlueprintSource) {
      this._saveState = 'error';
      this._saveError = new ServiceBlueprintSaveError({
        title: 'Save unavailable',
        summary: 'No service blueprint source is wired to the editor.',
        detailLines: ['Connect a service blueprint source before trying to save.'],
      });
      this._saveMessage = this._saveError.summary;
      this._saveErrorCopyStatus = null;
      return;
    }

    try {
      await this.serviceBlueprintSource.save(this.blueprintKey, this._serviceBlueprint);
      // A successful save (no conflict thrown) means expectedVersion — this._serviceBlueprint.version at
      // the time of the call — matched what the store had, and every IServiceBlueprintSourceStore
      // increments by exactly 1 on that path. serviceBlueprintSource.save() returns void, not the new
      // version, so bump it locally rather than leaving _serviceBlueprint.version stale: left unbumped,
      // the next _pollServiceBlueprintVersion (15s later) compares that stale local version against the
      // real server version and false-positives "someone else changed this" against the editor's
      // own save.
      this._serviceBlueprint = cloneServiceBlueprint({ ...this._serviceBlueprint, version: this._serviceBlueprint.version + 1 });
      this._savedServiceBlueprintSnapshot = cloneServiceBlueprint(this._serviceBlueprint);
      this._saveState = 'saved';
      this._saveMessage = 'Service blueprint saved.';
      this._saveError = null;
      this._saveErrorCopyStatus = null;
      this._showToast(this._saveMessage);
    } catch (error) {
      const normalised = normaliseServiceBlueprintSaveError(
        error,
        'The editor couldn’t save your changes. Review the details below and try again.'
      );

      if (normalised.isConflict) {
        // Same treatment as a proactively-detected staleness — one consistent path (read-only
        // overlay + banner) regardless of whether we found out via polling or via this failed save.
        this._saveState = 'idle';
        this._saveMessage = null;
        this._saveError = null;
        this._saveErrorCopyStatus = null;
        this._staleness.markStale(normalised.currentVersion);
        return;
      }

      this._saveState = 'error';
      this._saveError = normalised;
      this._saveMessage = this._saveError.summary;
      this._saveErrorCopyStatus = null;
    }
  }

  private async _handleReloadAfterConflict() {
    this._saveState = 'idle';
    this._saveMessage = null;
    this._saveError = null;
    this._saveErrorCopyStatus = null;
    // Deliberately NOT clearing _serviceBlueprintStale here — that must stay true (read-only
    // overlay up, banner's Reload button available) until _loadServiceBlueprint actually succeeds.
    // _initialiseEditorState clears it on success. If the reload itself fails, we're
    // correctly still stale/read-only rather than briefly unlocked with old content.
    await this._loadServiceBlueprint();
    if (!this._staleness.stale) {
      this._showToast('Reloaded the latest version.');
    }
  }

  private async _copySaveErrorDetails() {
    if (!this._saveError) {
      return;
    }

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(this._saveError.copyText);
        this._saveErrorCopyStatus = 'Save error details copied.';
        return;
      }
    } catch {
      // Fall through to manual copy support below.
    }

    const copyField = this.shadowRoot?.querySelector<HTMLTextAreaElement>('[data-wayfinder-save-error-details]');
    copyField?.focus();
    copyField?.select();
    this._saveErrorCopyStatus = 'Clipboard access is unavailable. Select and copy the details manually.';
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

  private _renderValidationPanel() {
    if (!this._serviceBlueprint) {
      return html`<div class="validation-empty-panel">No serviceBlueprint loaded</div>`;
    }

    const issues = this._validation.issues;
    const errorCount = this._validation.blocking.length;
    const warningCount = this._validation.warnings.length;

    return html`
      <section class="validation-panel" aria-labelledby="service-blueprint-validation-panel-title" data-wayfinder-validation-rail>
        <div class="validation-panel-header">
          <div>
            <h2 id="service-blueprint-validation-panel-title" class="validation-panel-title">Service Blueprint validation</h2>
            <p class="validation-panel-summary">${this._validation.summary}</p>
          </div>
          <div class="validation-panel-meta">
            ${this._validation.pending ? html`<span class="validation-count" data-wayfinder-validation-pending>checking…</span>` : nothing}
            <span class="validation-count validation-count-error" data-wayfinder-validation-errors>${errorCount} errors</span>
            <span class="validation-count validation-count-warning" data-wayfinder-validation-warnings>${warningCount} warnings</span>
          </div>
        </div>

        <div class="validation-panel-save-status" data-wayfinder-save-status>
          <span class="validation-save-label">Save status</span>
          <span>${this._saveStatusSummary}</span>
        </div>

        ${
          issues.length === 0
            ? html`<p class="validation-empty">No validation issues. You can save whenever you are ready.</p>`
            : html`
              <ol class="validation-issue-list">
                ${issues.map(
                  (issue) => html`
                  <li>
                    <button
                      type="button"
                      class="validation-issue-link"
                      data-wayfinder-validation-issue=${issue.id}
                      @click=${() => this._jumpToValidationIssue(issue)}
                    >
                      <span class=${`validation-issue-badge validation-issue-badge-${issue.severity}`}>
                        ${issue.severity === 'error' ? 'Error' : 'Warning'}
                      </span>
                      <span>${issue.message}</span>
                    </button>
                  </li>
                `
                )}
              </ol>
            `
        }
      </section>
    `;
  }

  private _renderShortcutGuide() {
    if (!this._helpOpen) {
      return nothing;
    }

    return html`
      <div
        class="modal-backdrop"
        role="presentation"
        @click=${(event: MouseEvent) => {
          if (event.target === event.currentTarget) {
            this._closeShortcutGuide();
          }
        }}
      >
        <section
          class="shortcut-dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby="service-blueprint-shortcut-title"
          aria-describedby="service-blueprint-shortcut-copy"
          data-wayfinder-shortcut-dialog
          @keydown=${(event: KeyboardEvent) => this._handleDialogKeydown(event, () => this._closeShortcutGuide())}
        >
          <div class="shortcut-dialog-header">
            <div>
              <p class="shortcut-dialog-eyebrow">Help and shortcuts</p>
              <h2 id="service-blueprint-shortcut-title" class="shortcut-dialog-title">Service Blueprint editor keyboard reference</h2>
              <p id="service-blueprint-shortcut-copy" class="shortcut-dialog-copy">
                These shortcuts stay visible in the editor so authors do not have to memorise them. Open this guide any time with F1.
              </p>
            </div>
            <button
              type="button"
              class="toolbar-btn shortcut-dialog-close"
              data-wayfinder-help-close
              @click=${() => this._closeShortcutGuide()}
            >
              Close
            </button>
          </div>

          <div class="shortcut-groups">
            ${SERVICE_BLUEPRINT_SHORTCUT_GROUPS.map(
              (group) => html`
              <section class="shortcut-group" data-wayfinder-shortcut-group=${group.id}>
                <h3 class="shortcut-group-title">${group.title}</h3>
                <ol class="shortcut-list">
                  ${group.shortcuts.map(
                    (shortcut) => html`
                    <li class="shortcut-item" data-wayfinder-shortcut=${shortcut.id}>
                      <div class="shortcut-copy">
                        <p class="shortcut-command">${shortcut.command}</p>
                        <p class="shortcut-description">${shortcut.description}</p>
                      </div>
                      <div class="shortcut-keys" aria-label=${`${shortcut.command} shortcuts`}>
                        ${shortcut.labels.map((label) => html`<kbd>${label}</kbd>`)}
                      </div>
                      <p class="shortcut-context">${shortcut.context}</p>
                    </li>
                  `
                  )}
                </ol>
              </section>
            `
            )}
          </div>

          <section class="shortcut-group" data-wayfinder-shortcut-group="quick-tips">
            <h3 class="shortcut-group-title">Quick tips</h3>
            <ul class="help-tip-list">
              <li>Each queue is one <strong>vertical service column</strong>. Read the service blueprint <strong>top to bottom</strong>.</li>
              <li>Stages are the work cards. Gateways are the diamond routing points between them.</li>
              <li>Use the <strong>Outline</strong> panel on the left to jump between queue columns and stages quickly.</li>
              <li>Reorder stages in <strong>List view</strong> with <strong>Move up</strong>, <strong>Move down</strong>, or <strong>Alt + Arrow</strong>. The canvas keeps its automatic layout in this first pass.</li>
              <li>Use the <strong>Validation</strong> tab to check for issues before you save.</li>
              <li>All structural changes support <strong>Undo/Redo</strong> — experiment safely.</li>
            </ul>
          </section>

          <section class="shortcut-group" data-wayfinder-shortcut-group="getting-started">
            <h3 class="shortcut-group-title">Getting started</h3>
            <ol class="help-tip-list">
              <li>Start on the <strong>Canvas</strong> tab and add the first stage for the queue that owns the work.</li>
              <li>Add the next stage that should happen in the service flow, then open the <strong>Inspector</strong> to shape its details.</li>
              <li>Add a <strong>routing gateway</strong> when the service blueprint needs to branch or wait for multiple paths to join.</li>
              <li>Create routes so the canvas reads as <strong>stage → gateway → stage</strong> or <strong>gateway → gateway</strong>.</li>
              <li>Check <strong>Validation</strong> before saving.</li>
              <li>Save your service blueprint when ready — changes will be published to the runtime.</li>
            </ol>
          </section>
        </section>
      </div>
    `;
  }

  private get _canSaveByContext(): boolean {
    return this.authorContext?.canSave !== false;
  }

  private _toggleOutlineCollapsed = () => {
    this._outlineCollapsed = !this._outlineCollapsed;
  };

  private _toggleInspectorCollapsed = () => {
    this._inspectorCollapsed = !this._inspectorCollapsed;
  };

  private _clampInspectorWidth(width: number): number {
    const minWidth = 280;
    const maxWidth = 720;
    return Math.min(maxWidth, Math.max(minWidth, width));
  }

  // The Properties panel sits on the right, so dragging the handle left (a shrinking clientX)
  // should widen it — width tracks the *negative* of the pointer's horizontal movement.
  private _handleInspectorResizeStart = (event: PointerEvent) => {
    event.preventDefault();
    this._inspectorResizeStartX = event.clientX;
    this._inspectorResizeStartWidth = this._inspectorWidth;
    this._inspectorResizing = true;
    window.addEventListener('pointermove', this._handleInspectorResizeMove);
    window.addEventListener('pointerup', this._handleInspectorResizeEnd);
  };

  private _handleInspectorResizeMove = (event: PointerEvent) => {
    const delta = this._inspectorResizeStartX - event.clientX;
    this._inspectorWidth = this._clampInspectorWidth(this._inspectorResizeStartWidth + delta);
  };

  private _handleInspectorResizeEnd = () => {
    this._inspectorResizing = false;
    window.removeEventListener('pointermove', this._handleInspectorResizeMove);
    window.removeEventListener('pointerup', this._handleInspectorResizeEnd);
  };

  private _handleInspectorResizeKeydown = (event: KeyboardEvent) => {
    const step = 16;
    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      this._inspectorWidth = this._clampInspectorWidth(this._inspectorWidth + step);
    } else if (event.key === 'ArrowRight') {
      event.preventDefault();
      this._inspectorWidth = this._clampInspectorWidth(this._inspectorWidth - step);
    }
  };

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
        ${this._renderToast()}
        ${this._loading ? html`<div class="loading-banner" role="status">Loading serviceBlueprint…</div>` : nothing}
        ${this._error ? html`<div class="error-banner" role="alert">${this._error}</div>` : nothing}
        ${this._renderSaveErrorSurface()}
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
              class="toolbar-btn toolbar-btn--icon govuk-button${this._saveState === 'saving' ? ' toolbar-btn--spinning' : ''}"
              data-wayfinder-save
              ?disabled=${!this._canSave}
              aria-label=${this._saveState === 'saving' ? 'Saving' : 'Save'}
              title=${
                !this._canSaveByContext
                  ? 'Saving is disabled for the current author.'
                  : `${this._dirtyStateSummary} — ${this._saveState === 'saving' ? 'Saving…' : 'Save'}${SAVE_SHORTCUT ? ` (${SAVE_SHORTCUT.labels[0]})` : ''}`
              }
              aria-keyshortcuts=${SAVE_SHORTCUT?.ariaKeys ?? nothing}
              @click=${this._handleSave}
            >
              ${this._saveState === 'saving' ? renderToolbarIcon('saving') : renderToolbarIcon('save')}
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
                      @click=${(event: Event) => this._openShortcutGuide(event.currentTarget as HTMLElement)}
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
              class=${`editor-shell ${this._inspectorResizing ? 'editor-shell-resizing' : ''}`}
              style=${`--outline-width:${this._outlineCollapsed ? '3.5rem' : '240px'};--inspector-width:${this._inspectorCollapsed ? '3.5rem' : `${this._inspectorWidth}px`};`}
            >
              <!-- Left: outline -->
              <section class=${`editor-outline-shell ${this._outlineCollapsed ? 'panel-collapsed' : ''}`}>
                <div class="panel-header">
                  <div class="panel-header-copy">
                    <h2 class="panel-title">Outline</h2>
                    ${
                      this._outlineCollapsed
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
                    aria-expanded=${String(!this._outlineCollapsed)}
                    aria-label=${this._outlineCollapsed ? 'Expand outline panel' : 'Collapse outline panel'}
                    @click=${this._toggleOutlineCollapsed}
                  >
                    ${this._outlineCollapsed ? renderToolbarIcon('chevronRight') : renderToolbarIcon('chevronLeft')}
                    <span class="sr-only">${this._outlineCollapsed ? 'Expand outline' : 'Collapse outline'}</span>
                  </button>
                </div>
                <div
                  id="service-blueprint-editor-outline-panel"
                  class="panel-body"
                  ?hidden=${this._outlineCollapsed}
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
              <section class=${`editor-right ${this._inspectorCollapsed ? 'panel-collapsed' : ''}`}>
                ${
                  this._inspectorCollapsed
                    ? nothing
                    : html`
                      <div
                        class="panel-resize-handle"
                        role="separator"
                        aria-orientation="vertical"
                        aria-label="Resize properties panel"
                        aria-valuenow=${this._inspectorWidth}
                        aria-valuemin="280"
                        aria-valuemax="720"
                        tabindex="0"
                        @pointerdown=${this._handleInspectorResizeStart}
                        @keydown=${this._handleInspectorResizeKeydown}
                      ></div>
                    `
                }
                <div class="panel-header">
                  <div class="panel-header-copy">
                    <h2 class="panel-title">Properties</h2>
                    ${this._inspectorCollapsed ? nothing : html`<p class="panel-subtitle">Selected stage, gateway, or route details</p>`}
                  </div>
                  <button
                    type="button"
                    class="panel-toggle"
                    data-wayfinder-inspector-toggle
                    aria-controls="service-blueprint-editor-inspector-panel"
                    aria-expanded=${String(!this._inspectorCollapsed)}
                    aria-label=${this._inspectorCollapsed ? 'Expand properties drawer' : 'Collapse properties drawer'}
                    @click=${this._toggleInspectorCollapsed}
                  >
                    ${this._inspectorCollapsed ? renderToolbarIcon('chevronLeft') : renderToolbarIcon('chevronRight')}
                    <span class="sr-only">${this._inspectorCollapsed ? 'Expand properties drawer' : 'Collapse properties drawer'}</span>
                  </button>
                </div>
                <div
                  id="service-blueprint-editor-inspector-panel"
                  class="panel-body"
                  ?hidden=${this._inspectorCollapsed}
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
          <div slot="validation">${this._renderValidationPanel()}</div>
          <div slot="definition">${this._definition.renderPanel()}</div>
        </wayfinder-confidence-tabs>
        </div>

        ${this._renderShortcutGuide()}
      </div>
    `;
  }

  private _renderToast() {
    if (!this._toastMessage) return nothing;
    return html`
      <div
        class="toast-banner"
        role="status"
        aria-live="assertive"
        data-wayfinder-toast
      >
        ${this._toastMessage}
      </div>
    `;
  }

  private _renderSaveErrorSurface() {
    if (!this._saveError) {
      return nothing;
    }

    return html`
      <section
        class="save-error-surface"
        aria-labelledby="service-blueprint-save-error-title"
        tabindex="-1"
        data-wayfinder-save-error
      >
        <div class="save-error-header">
          <p class="save-error-eyebrow">Save problem</p>
          <h2 id="service-blueprint-save-error-title" class="save-error-title">${this._saveError.title}</h2>
          ${
            this._saveError.summaryStageKey
              ? html`
                <p class="save-error-summary" role="alert">
                  <button
                    type="button"
                    class="save-error-detail-link"
                    data-wayfinder-save-error-jump
                    @click=${() => this._jumpToStage(this._saveError!.summaryStageKey!)}
                  >
                    ${this._saveError.summary}
                    <span class="save-error-detail-link-hint">Go to stage</span>
                  </button>
                </p>
              `
              : html`<p class="save-error-summary" role="alert">${this._saveError.summary}</p>`
          }
        </div>

        ${
          this._saveError.details.length > 0
            ? html`
              <ul class="save-error-list">
                ${this._saveError.details.map(
                  (detail) => html`
                  <li>
                    ${
                      detail.stageKey
                        ? html`
                          <button
                            type="button"
                            class="save-error-detail-link"
                            data-wayfinder-save-error-jump
                            @click=${() => this._jumpToStage(detail.stageKey!)}
                          >
                            ${detail.message}
                            <span class="save-error-detail-link-hint">Go to stage</span>
                          </button>
                        `
                        : detail.message
                    }
                  </li>
                `
                )}
              </ul>
            `
            : nothing
        }

        ${this._saveError.traceId ? html`<p class="save-error-trace"><strong>Reference:</strong> ${this._saveError.traceId}</p>` : nothing}

        <label class="save-error-copy-label" for="service-blueprint-save-error-details">Copyable save error details</label>
        <textarea
          id="service-blueprint-save-error-details"
          class="save-error-copy-field"
          readonly
          rows="6"
          .value=${this._saveError.copyText}
          data-wayfinder-save-error-details
        ></textarea>

        <div class="save-error-actions">
          <button
            type="button"
            class="toolbar-btn govuk-button govuk-button--secondary save-error-copy-button"
            data-wayfinder-copy-save-error
            @click=${this._copySaveErrorDetails}
          >
            Copy details
          </button>
          <button
            type="button"
            class="toolbar-btn govuk-button govuk-button--secondary"
            aria-label="Dismiss save error"
            data-wayfinder-dismiss-save-error
            @click=${() => {
              this._saveError = null;
              this._saveErrorCopyStatus = null;
            }}
          >
            Dismiss
          </button>
          <p class="save-error-copy-status" role="status" aria-live="polite" data-wayfinder-save-error-copy-status>
            ${this._saveErrorCopyStatus ?? ''}
          </p>
        </div>
      </section>
    `;
  }

  static styles = unsafeCSS(editorStyles);
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-service-blueprint-editor': WayfinderServiceBlueprintEditorElement;
  }
}
