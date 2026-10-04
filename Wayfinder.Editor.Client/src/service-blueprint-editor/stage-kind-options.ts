import type { StageKind } from './types.js';

/** The stage kinds an author can pick, in the order and wording the editor offers them. */
export const STAGE_KIND_OPTIONS: ReadonlyArray<{ value: StageKind; label: string }> = [
  { value: 'Question', label: 'Form' },
  { value: 'CheckAnswers', label: 'Review' },
  { value: 'TaskList', label: 'Decision' },
  { value: 'Confirmation', label: 'Confirmation' },
];

export function stageKindLabel(kind: StageKind): string {
  return STAGE_KIND_OPTIONS.find((option) => option.value === kind)?.label ?? kind;
}
