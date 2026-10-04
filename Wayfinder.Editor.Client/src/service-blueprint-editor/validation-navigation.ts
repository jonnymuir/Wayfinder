import type { ServiceBlueprint } from './types.js';
import type { SelectionController } from './editor-selection.js';
import type { ServiceBlueprintValidationIssue, ServiceBlueprintValidationLocation } from './service-blueprint-validation.js';
import { flattenRoutes } from './route-model.js';
import type { ConfidenceTab } from './wayfinder-confidence-tabs.js';

type ActionLocation = Extract<ServiceBlueprintValidationLocation, { kind: 'action' }>;

export interface ValidationNavigationContext {
  blueprint(): ServiceBlueprint | null;
  selection: SelectionController;
  showTab(tab: ConfidenceTab): void;
  revealInspector(): void;
  inspector(): HTMLElement | null;
}

/** The control inside the action editor that a validation issue is about, most specific first. */
function actionControlSelector(location: ActionLocation): string {
  if (location.fieldKey && location.fieldKey !== 'fields') {
    return `[data-wayfinder-action-param="${location.actionIndex}-${location.fieldKey}"]`;
  }
  if (typeof location.formFieldIndex === 'number') {
    return `[data-wayfinder-form-field-key="${location.actionIndex}-${location.formFieldIndex}"]`;
  }
  return `[data-wayfinder-stage-action="${location.actionIndex}"]`;
}

/**
 * Takes the author from an entry in the validation rail (or a save-time diagnostic) to the thing it is
 * about: the stage, route or action on the canvas, with focus moved into the Properties panel.
 */
export class ValidationNavigator {
  constructor(private readonly _context: ValidationNavigationContext) {}

  /**
   * Selecting the stage is enough to guide someone to a save-time diagnostic; the message itself
   * (already shown in the save-error list) names the specific component and field.
   */
  jumpToStage(stageKey: string): void {
    if (!this._context.blueprint()) {
      return;
    }
    this._showCanvas();
    this._context.selection.apply({ kind: 'stage', stageKey });
    this._context.selection.clearAction();
  }

  jump(issue: ServiceBlueprintValidationIssue): void {
    if (!this._context.blueprint()) {
      return;
    }

    const { location } = issue;
    switch (location.kind) {
      case 'calculation':
        this._context.showTab('calculations');
        return;
      case 'document':
        // A server diagnostic that names nothing navigable — leave the view where it is.
        return;
      case 'stage':
        this._showCanvas();
        this._context.selection.apply({ kind: 'stage', stageKey: location.stageKey });
        this._context.selection.clearAction();
        this._focusInspector(issue);
        return;
      case 'route':
        this._showCanvas();
        this._highlightRoute(location.routeId);
        this._context.selection.clearAction();
        this._focusInspector(issue);
        return;
      case 'action':
        this._showCanvas();
        this._jumpToAction(location);
        this._focusInspector(issue);
        return;
    }
  }

  private _showCanvas() {
    this._context.showTab('canvas');
    this._context.revealInspector();
  }

  private _routeIndex(routeId: string | undefined): number {
    return flattenRoutes(this._context.blueprint()).findIndex((view) => view.routeId === routeId);
  }

  private _highlightRoute(routeId: string) {
    const index = this._routeIndex(routeId);
    if (index >= 0) {
      this._context.selection.highlightTransition(index);
    }
  }

  private _jumpToAction(location: ActionLocation) {
    const { selection } = this._context;
    if (location.target === 'route') {
      selection.highlightTransition(Math.max(this._routeIndex(location.routeId), 0));
      selection.chooseAction({ target: 'transition', index: location.actionIndex });
    } else {
      selection.apply({ kind: 'stage', stageKey: location.stageKey ?? '' });
      selection.chooseAction({ target: 'stage', index: location.actionIndex });
    }
  }

  private _focusInspector(issue: ServiceBlueprintValidationIssue) {
    const location = issue.location.kind === 'action' ? issue.location : null;
    this._context.revealInspector();
    requestAnimationFrame(() => {
      const inspector = this._context.inspector();
      inspector?.focus();
      if (!location) {
        return;
      }
      requestAnimationFrame(() => {
        const actionEditor = inspector?.shadowRoot?.querySelector<HTMLElement>('wayfinder-stage-action-editor');
        actionEditor?.shadowRoot?.querySelector<HTMLElement>(actionControlSelector(location))?.focus();
      });
    });
  }
}
