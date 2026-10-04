import type { ComponentDescriptor, ServiceBlueprintCalculationSet, StageDefinition } from '../types.js';
import type { FieldReference } from '../component-property-references.js';
import type { computeCalculationDiagnostics } from '../calculation-diagnostics.js';
import type { ExpressionCompletionItem } from '../wayfinder-calculation-expression-editor.js';

/** What each section of the Calculations tab reads from, and reports changes through, the tab. */
export interface CalculationsContext {
  readonly calculations: ServiceBlueprintCalculationSet;
  /** Every input captured anywhere in the blueprint, which a calculation may reference. */
  readonly inputFields: FieldReference[];
  /** Each input's declared default, coerced the way the calculation scope expects, for the live previews. */
  readonly sampleInputs: Record<string, unknown>;
  readonly diagnostics: ReturnType<typeof computeCalculationDiagnostics>;
  readonly stages: StageDefinition[];
  readonly componentCatalog: ComponentDescriptor[];
  updateCalculations(next: ServiceBlueprintCalculationSet): void;
  updateStage(stageKey: string, patch: Partial<StageDefinition>): void;
  announce(message: string): void;
}

/** The names an expression in this tab may insert: inputs, and the extra named things (fields, tables…) it is given. */
export function completionsFor(
  context: CalculationsContext,
  ...named: Array<{ names: string[]; detail: string }>
): ExpressionCompletionItem[] {
  return [
    ...context.inputFields.map((input) => ({ name: input.fieldKey, detail: input.label })),
    ...named.flatMap((group) => group.names.map((name) => ({ name, detail: group.detail }))),
  ];
}
