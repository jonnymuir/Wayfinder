/**
 * Reference data for the properties-panel's "reference-aware" `<select>` fields (see
 * BuiltInComponentDescriptors.cs's `Format` tags: `field-ref`/`conditional-value-ref`/
 * `own-options-ref`/`calculation-ref`/`stage-ref`/`field-or-calc-ref`). None of these are new
 * server data — `ConditionalOn`/`VisibleWhen` are only ever checked against the *current stage's
 * own* submitted field values (Wayfinder/Services/Validation/FieldValueValidator.cs), `DefaultFrom`
 * resolves against ServiceBlueprint.Calculations.Fields (blueprint-wide), and `ChangeStateKey`
 * should be a real stage key — all of it already sits in `AuthoredServiceBlueprint`, which
 * wayfinder-step-inspector.ts already holds in full. This module just walks that data into the
 * lists the properties panel offers instead of a blank text box.
 */

import type { AuthoredComponent, AuthoredServiceBlueprint, ComponentDescriptor } from './types.js';

export interface FieldReference {
  fieldKey: string;
  label: string;
  options?: string[];
  type: string;
  /** The field's own declared default (always a raw string on the wire, regardless of the
   * field's real value kind — see BuiltInComponentDescriptors.cs's InputComponent.Default).
   * Used by the Calculations tab to seed a live-preview sample scope, the same source
   * `validate_service_blueprint`'s own static check relies on. */
  default?: string;
}

export interface PropertyReferenceContext {
  /** Input fields in the *current stage only* — ConditionalOn/VisibleWhen are only ever checked
   * against a stage's own submitted values (FieldValueValidator.cs), so a value here is the only
   * kind that can ever actually match at runtime. */
  siblingFields: FieldReference[];
  /** Input fields across the *whole blueprint* — for bindings that read from the full instance
   * (StatItemDefinition.FieldKey, matching ServiceBlueprint.ValidateDataDisplayBindings' own
   * "calculated field or any input field, blueprint-wide" scope), typically a later stage
   * (Confirmation/CheckAnswers) referencing a value captured on an earlier one. */
  allFields: FieldReference[];
  stageOptions: Array<{ key: string; label: string }>;
  calculationFieldNames: string[];
}

/**
 * Every input field in `components`, recursing through container children the same
 * `containment.kind`/`propertyName`/`sectionChildrenPropertyName`/`keySourceProperty`-driven way
 * service-blueprint-lint.ts's `lintComponentTree` already does (catalog-driven, not a hand-rolled
 * switch over component type names) — a field nested inside a fieldset, an accordion section, or
 * a radio's conditional children is still a real sibling whose value is submitted with the rest
 * of the stage.
 */
export function collectStageInputFields(
  components: AuthoredComponent[] | undefined,
  catalog: ComponentDescriptor[]
): FieldReference[] {
  const results: FieldReference[] = [];
  walkComponents(components, catalog, results);
  return results;
}

function walkComponents(
  components: AuthoredComponent[] | undefined,
  catalog: ComponentDescriptor[],
  results: FieldReference[]
): void {
  if (!Array.isArray(components)) {
    return;
  }

  for (const component of components) {
    const descriptor = catalog.find(candidate => candidate.discriminator === component.type);
    const record = component as unknown as Record<string, unknown>;

    if (descriptor?.isInput && typeof record.fieldKey === 'string') {
      const options = Array.isArray(record.options) ? (record.options as unknown[]).map(String) : undefined;
      results.push({
        fieldKey: record.fieldKey,
        label: typeof record.label === 'string' && record.label ? record.label : record.fieldKey,
        options,
        type: component.type,
        default: typeof record.default === 'string' ? record.default : undefined,
      });
    }

    // A DataDisplay container's children (e.g. SummaryListComponent.Children, GOV.UK's
    // check-your-answers pattern) reuse an input-shaped CLR type purely for rendering
    // convenience — they never receive a submission of their own, only ever echo a value
    // captured elsewhere under the same fieldKey. Descending into them here duplicated every
    // echoed field once per summary-list/stat-group it appeared in (confirmed live: a real
    // field echoed on two summary-lists plus its own real input showed "triplicate" in the
    // reference picker) — mirrors ComponentExtensions.cs's GetSubmittableInputs, which stops at
    // exactly this same boundary for exactly this same reason.
    if (descriptor?.category === 'DataDisplay') {
      continue;
    }

    const containment = descriptor?.containment;
    if (!containment || containment.kind === 'None' || !containment.propertyName) {
      continue;
    }

    if (containment.kind === 'ChildList') {
      walkComponents(record[containment.propertyName] as AuthoredComponent[] | undefined, catalog, results);
    } else if (containment.kind === 'NamedSections') {
      const sections = record[containment.propertyName];
      const childrenKey = containment.sectionChildrenPropertyName ?? 'children';
      if (Array.isArray(sections)) {
        for (const section of sections as Array<Record<string, unknown>>) {
          walkComponents(section[childrenKey] as AuthoredComponent[] | undefined, catalog, results);
        }
      }
    } else if (containment.kind === 'KeyedChildren') {
      const byKey = record[containment.propertyName];
      if (byKey && typeof byKey === 'object') {
        for (const children of Object.values(byKey as Record<string, AuthoredComponent[]>)) {
          walkComponents(children, catalog, results);
        }
      }
    }
  }
}

export function buildPropertyReferenceContext(
  serviceBlueprint: AuthoredServiceBlueprint | null | undefined,
  stageComponents: AuthoredComponent[] | undefined,
  catalog: ComponentDescriptor[]
): PropertyReferenceContext {
  const allComponents = (serviceBlueprint?.stages ?? []).flatMap(stage => stage.components ?? []);

  return {
    siblingFields: collectStageInputFields(stageComponents, catalog),
    allFields: collectStageInputFields(allComponents, catalog),
    stageOptions: (serviceBlueprint?.stages ?? []).map(stage => ({
      key: stage.stateKey,
      label: `${stage.displayName} (${stage.stateKey})`,
    })),
    calculationFieldNames: Object.keys(serviceBlueprint?.calculations?.fields ?? {}),
  };
}
