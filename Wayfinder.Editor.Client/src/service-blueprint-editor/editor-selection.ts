import type { ReactiveController, ReactiveControllerHost } from 'lit';
import type { ActionDefinition, ServiceBlueprint } from './types.js';
import { flattenRoutes } from './route-model.js';

/** What is selected on the canvas. A route is never selected on its own: it is shown through the stage or gateway that owns it. */
export type EditorSelection = { kind: 'stage'; stageKey: string } | { kind: 'gateway'; gatewayKey: string } | null;

/** An action in the selected stage's, or the highlighted route's, action list. */
export type ActionSelection = { target: 'stage' | 'transition'; index: number } | null;

/** A selection as the graph, outline and inspector report it, including the route they can also name. */
export type SelectionDetail = {
  kind: 'stage' | 'gateway' | 'transition';
  stageKey?: string;
  gatewayKey?: string;
  transitionIndex?: number;
} | null;

export function cloneSelection(selection: EditorSelection): EditorSelection {
  return selection ? { ...selection } : null;
}

const selectionKey = (selection: EditorSelection): string | null => {
  if (selection?.kind === 'stage') return `stage:${selection.stageKey}`;
  return selection?.kind === 'gateway' ? `gateway:${selection.gatewayKey}` : null;
};

export function selectionsEqual(left: EditorSelection, right: EditorSelection): boolean {
  return selectionKey(left) === selectionKey(right);
}

/** The selection a graph/outline/inspector event describes, or null for anything the editor cannot select (a route has its own path). */
export function selectionFromDetail(detail: SelectionDetail | undefined): EditorSelection {
  if (detail?.kind === 'stage' && detail.stageKey) {
    return { kind: 'stage', stageKey: detail.stageKey };
  }
  if (detail?.kind === 'gateway' && detail.gatewayKey) {
    return { kind: 'gateway', gatewayKey: detail.gatewayKey };
  }
  return null;
}

function exists(selection: EditorSelection, blueprint: ServiceBlueprint): boolean {
  if (selection?.kind === 'stage') {
    return blueprint.stages.some((stage) => stage.stageKey === selection.stageKey);
  }
  if (selection?.kind === 'gateway') {
    return blueprint.gateways?.some((gateway) => gateway.key === selection.gatewayKey) ?? false;
  }
  return false;
}

export interface SelectionContext {
  blueprint(): ServiceBlueprint | null;
  /** Called whenever something becomes selected, so the Properties panel is open for it. */
  revealInspector(): void;
}

/**
 * What the author has selected: a stage or gateway, optionally with one of its routes highlighted and
 * one action within the stage or route chosen. Owns the rules for keeping that consistent with the
 * blueprint (a selection that no longer exists clears; selecting something new drops the old action).
 */
export class SelectionController implements ReactiveController {
  private _selection: EditorSelection = null;
  private _transitionIndex: number | null = null;
  private _action: ActionSelection = null;

  constructor(
    private readonly _host: ReactiveControllerHost,
    private readonly _context: SelectionContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  get current(): EditorSelection {
    return this._selection;
  }

  get stageKey(): string | null {
    return this._selection?.kind === 'stage' ? this._selection.stageKey : null;
  }

  get gatewayKey(): string | null {
    return this._selection?.kind === 'gateway' ? this._selection.gatewayKey : null;
  }

  get transitionIndex(): number | null {
    return this._transitionIndex;
  }

  /** The chosen action's index, but only while it belongs to the selected stage (the inspector's own list). */
  get stageActionIndex(): number | null {
    return this._selection?.kind === 'stage' && this._action?.target === 'stage' ? this._action.index : null;
  }

  /** The chosen action itself, for copy. */
  currentAction(): { action: ActionDefinition; target: 'stage' | 'transition' } | null {
    const blueprint = this._context.blueprint();
    if (!blueprint || !this._action) {
      return null;
    }

    const { target, index } = this._action;
    if (target === 'stage' && this.stageKey) {
      const action = blueprint.stages.find((stage) => stage.stageKey === this.stageKey)?.actions?.[index];
      return action ? { action, target } : null;
    }
    if (target === 'transition' && this._transitionIndex !== null) {
      const action = flattenRoutes(blueprint)[this._transitionIndex]?.actions?.[index];
      return action ? { action, target } : null;
    }
    return null;
  }

  /** Selects `selection` if it still exists in `blueprint`; otherwise clears the selection. Drops any highlighted route. */
  apply(selection: EditorSelection, blueprint: ServiceBlueprint | null = this._context.blueprint()): void {
    const valid = blueprint && exists(selection, blueprint);
    this._selection = valid ? cloneSelection(selection) : null;
    this._transitionIndex = null;
    if (this._selection) {
      this._context.revealInspector();
    }
    this._host.requestUpdate();
  }

  /**
   * Highlights a route. The inspector has no standalone route view — a route is only shown inside the
   * stage or gateway whose routes it belongs to — so that owner is selected too; without it a
   * newly-connected or outline-clicked route would never become editable.
   */
  highlightTransition(index: number, blueprint: ServiceBlueprint | null = this._context.blueprint()): void {
    const route = blueprint ? flattenRoutes(blueprint)[index] : undefined;
    if (!route) {
      this._transitionIndex = null;
      this._host.requestUpdate();
      return;
    }

    this._selection = route.fromGateway ? { kind: 'gateway', gatewayKey: route.fromGateway } : { kind: 'stage', stageKey: route.fromStage };
    this._transitionIndex = index;
    this._context.revealInspector();
    this._host.requestUpdate();
  }

  chooseAction(action: ActionSelection): void {
    this._action = action;
    this._host.requestUpdate();
  }

  clearAction(): void {
    this.chooseAction(null);
  }
}
