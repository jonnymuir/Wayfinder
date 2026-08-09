// Hand-written type declarations for wayfinder-calculations.js — a plain, no-build-step ES
// module (see that file's own doc comment). This file is a pure dev-time TypeScript artifact:
// it has zero effect on the .NET package or anything that isn't a TypeScript consumer of the
// .js file directly (currently: Wayfinder.Editor.Client's calculation-runtime.ts).

export declare class CalculationError extends Error {}

/** Fixed-point decimal value (BigInt mantissa, 12 decimal places) — see the .js file's own doc comment. */
export declare class Dec {
  m: bigint;
  static zero: Dec;
  static fromString(text: string): Dec;
  static fromNumber(value: number): Dec;
  toNumber(): number;
  toString(): string;
  add(other: Dec): Dec;
  sub(other: Dec): Dec;
  mul(other: Dec): Dec;
  div(other: Dec): Dec;
  neg(): Dec;
  abs(): Dec;
  floor(): Dec;
  round(places: number): Dec;
  cmp(other: Dec): number;
  eq(other: Dec): boolean;
}

export type CalculationToken = {
  kind: 'number' | 'identifier' | 'string' | 'op';
  value: string;
  position: number;
};

export type CalculationNode =
  | { kind: 'number'; value: Dec }
  | { kind: 'text'; value: string }
  | { kind: 'bool'; value: boolean }
  | { kind: 'identifier'; path: string }
  | { kind: 'unary'; op: string; operand: CalculationNode }
  | { kind: 'binary'; op: string; left: CalculationNode; right: CalculationNode }
  | { kind: 'call'; name: string; args: CalculationNode[] };

/** Table/field/series shapes matching ServiceBlueprintCalculationSet's JSON exactly (camelCase). */
export interface CalculationTableDefinition {
  interpolate?: string;
  values: Record<string, number>;
}

export interface CalculationFieldDefinition {
  expr?: string;
  source?: string;
  format?: string;
}

export interface CalculationSeriesDefinition {
  over: string;
  from: string;
  to: string;
  values: Record<string, string>;
}

export interface CalculationSet {
  tables?: Record<string, CalculationTableDefinition>;
  fields?: Record<string, CalculationFieldDefinition>;
  series?: Record<string, CalculationSeriesDefinition>;
}

/** Tokenizes a raw expression string — the same tokenizer the parser itself uses. */
export declare function tokenize(text: string): CalculationToken[];

/** Parses a single expression into its AST. Throws CalculationError on a syntax error. */
export declare function parseExpression(expression: string): CalculationNode;

/**
 * Evaluates a single standalone expression (e.g. a component's showWhen, or one
 * calculations.fields entry) against a scope that already contains inputs and any earlier
 * calculated fields. Tables from `set` are available to lookup(). Throws CalculationError.
 */
export declare function evaluateExpression(
  expression: string,
  scope: Record<string, unknown>,
  set?: CalculationSet
): unknown;

/** Converts host-supplied scope values (plain JSON) into evaluator values (Dec for numbers). */
export declare function toScope(inputs: Record<string, unknown>): Record<string, unknown>;

/**
 * Evaluates every field and series in `set` against `inputs`, in declaration order. Throws
 * CalculationError on the FIRST failure (a missing service-sourced field, an unresolvable name,
 * etc.) — there is no "collect every failure" variant in this runtime (unlike
 * CalculationEvaluator.EvaluateCollectingErrors in C#), so a single unresolvable field blocks
 * every result. Wayfinder.Editor.Client's calculation-runtime.ts instead orchestrates
 * evaluateExpression per field/series-row itself when it needs partial results.
 */
export declare function evaluateCalculations(
  set: CalculationSet,
  inputs: Record<string, unknown>
): { fields: Record<string, unknown>; series: Record<string, Array<Record<string, unknown>>> };
