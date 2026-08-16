/**
 * Route action-preset helpers for outgoing routes on a stage or gateway.
 *
 * This used to also carry an always/event/guard route-condition mini-language
 * (parse/serialise/describe helpers, prefixed condition strings) — replaced by
 * ServiceBlueprintRouteDefinition.ShowWhen, a plain calculation-language expression authored with
 * wayfinder-calculation-expression-editor directly (see wayfinder-step-inspector.ts's
 * _renderRouteEditor), the same as a stage validation's when/rule. That mini-language was never
 * evaluated anywhere in the engine, and — because the client serialised it under a "condition"
 * wire key the server model never had a matching property for — didn't even survive a save.
 */
import type { AuthoredServiceBlueprint } from './types.js';

export const TRANSITION_ACTION_OPTIONS = [
  { value: 'continue', label: 'Continue' },
  { value: 'submit', label: 'Submit' },
  { value: 'approve', label: 'Approve' },
  { value: 'reject', label: 'Reject' },
  { value: 'assign', label: 'Assign' },
  { value: 'return', label: 'Return' },
] as const;

export function transitionQuickAction(action?: string): string {
  return TRANSITION_ACTION_OPTIONS.some(option => option.value === (action ?? '').trim().toLowerCase())
    ? (action ?? '').trim().toLowerCase()
    : 'custom';
}

export function defaultTransitionTarget(serviceBlueprint: AuthoredServiceBlueprint, sourceStageKey: string): string | null {
  const currentIndex = serviceBlueprint.stages.findIndex(stage => stage.stateKey === sourceStageKey);
  if (currentIndex >= 0) {
    const nextStage = serviceBlueprint.stages[currentIndex + 1];
    if (nextStage) {
      return nextStage.stateKey;
    }
  }

  return serviceBlueprint.stages.find(stage => stage.stateKey !== sourceStageKey)?.stateKey ?? null;
}

export function defaultTransitionAction(serviceBlueprint: AuthoredServiceBlueprint, targetStageKey: string): string {
  return targetStageKey === serviceBlueprint.initialStage ? 'return' : 'continue';
}
