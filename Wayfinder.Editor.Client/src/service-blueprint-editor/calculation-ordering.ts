/**
 * Stable, minimal-reordering topological sort for calculations.fields — see the
 * "Field ordering is fully automatic" design decision in the calculations-tab plan.
 * `fields` must be declared before they're referenced (a forward reference is a hard error at
 * evaluation time — see docs/guides/calculation-language.md); asking a designer to maintain that
 * by hand is exactly the kind of thing that leads to errors by default. This computes the correct
 * order automatically, moving a field only when a real dependency requires it (never reshuffling
 * fields that have no relationship to what changed), and reports exactly what moved and why so an
 * automatic reorder is never a silent surprise. A genuine dependency cycle is reported by name
 * instead of silently producing an invalid order.
 *
 * Pure functions, no DOM/Lit dependency — deliberately, so this is exhaustively unit-testable in
 * isolation (see calculation-ordering.test.ts).
 */

import { extractReferencedNames } from './calculation-runtime.js';

export interface FieldInput {
  name: string;
  expr: string;
}

export interface FieldMove {
  name: string;
  /** The dependency that forced `name` to move after it. */
  movedAfter: string;
}

export type FieldOrderResult =
  | { ok: true; order: string[]; moved: FieldMove[] }
  | { ok: false; cycle: string[] };

/**
 * `currentOrder` is the field order before this change (e.g. the persisted declaration order,
 * or `[]` for a brand-new set) — used purely as a stability preference, not a correctness
 * requirement: any field named in `fields` but missing from `currentOrder` (a newly-added field)
 * is simply appended, in `fields`' own order, after every already-known field.
 */
export function computeStableFieldOrder(fields: FieldInput[], currentOrder: string[]): FieldOrderResult {
  const fieldNames = new Set(fields.map(f => f.name));

  const dependencies = new Map<string, Set<string>>();
  for (const field of fields) {
    const referenced = extractReferencedNames(field.expr);
    const fieldDeps = new Set<string>();
    if (referenced) {
      for (const name of referenced.scopeNames) {
        if (name !== field.name && fieldNames.has(name)) {
          fieldDeps.add(name);
        }
      }
    }
    dependencies.set(field.name, fieldDeps);
  }

  const priority = [
    ...currentOrder.filter(name => fieldNames.has(name)),
    ...fields.map(f => f.name).filter(name => !currentOrder.includes(name)),
  ];

  const placed = new Set<string>();
  const order: string[] = [];
  const remaining = new Set(priority);

  while (remaining.size > 0) {
    let picked: string | null = null;
    for (const name of priority) {
      if (!remaining.has(name)) {
        continue;
      }
      const fieldDeps = dependencies.get(name) ?? new Set();
      if ([...fieldDeps].every(dep => placed.has(dep))) {
        picked = name;
        break;
      }
    }

    if (picked === null) {
      return { ok: false, cycle: [...remaining] };
    }

    order.push(picked);
    placed.add(picked);
    remaining.delete(picked);
  }

  const moved: FieldMove[] = [];
  for (const field of fields) {
    const originalIndex = currentOrder.indexOf(field.name);
    if (originalIndex === -1) {
      continue;
    }

    let latestViolatingDep: string | null = null;
    let latestIndex = -1;
    for (const dep of dependencies.get(field.name) ?? []) {
      const depIndex = currentOrder.indexOf(dep);
      if (depIndex > originalIndex && depIndex > latestIndex) {
        latestViolatingDep = dep;
        latestIndex = depIndex;
      }
    }

    if (latestViolatingDep) {
      moved.push({ name: field.name, movedAfter: latestViolatingDep });
    }
  }

  return { ok: true, order, moved };
}
