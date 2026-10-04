import type { ComponentContainment, ComponentDescriptor, ComponentPropertyDescriptor } from '../types.js';
import { type DefinitionLint, type JsonObject, isJsonObject, lineOfString } from './lint-support.js';

/**
 * Blueprint-wide reference data for the dangling-reference checks — mirrors what
 * `ServiceBlueprint.ValidateFieldReferences` (Wayfinder/Models/ServiceDesign/ServiceBlueprint.cs)
 * checks server-side, so a mistake made by hand in the Definition tab is flagged before you even
 * try to save. `siblingFieldKeys` is stage-scoped (ConditionalOn/VisibleWhen are only ever checked
 * against the current stage's own submitted values); the rest are blueprint-wide.
 */
export interface ReferenceLintContext {
  siblingFieldKeys: Set<string>;
  calculationFieldNames: Set<string>;
  /** Property names declared in a service field's `shape`, by field name. */
  calculationShapes: Map<string, Set<string>>;
  stageKeys: Set<string>;
}

function isMissing(value: unknown): boolean {
  return (
    value === undefined ||
    value === null ||
    (typeof value === 'string' && value.trim() === '') ||
    (Array.isArray(value) && value.length === 0)
  );
}

/**
 * Live, as-you-type component validation in the Definition tab, mirroring
 * Wayfinder.Engine.Services.ComponentPropertyValidator's own checks (required/allowedValues/
 * pattern/length/numeric constraints, plus a KeyedChildren key not matching its sibling Options)
 * against the same live ComponentDescriptor catalog the properties-panel add/edit UI already
 * fetches. Recurses into a container's own children the same way component-child-editor.ts does,
 * so a nested component gets checked too, not just top-level ones.
 */
export class ComponentLinter {
  constructor(
    private readonly _catalog: ComponentDescriptor[],
    private readonly _source: string,
    private readonly _issues: DefinitionLint[],
    private readonly _refs?: ReferenceLintContext
  ) {}

  lintList(components: unknown, pathPrefix: string): void {
    if (!Array.isArray(components)) {
      return;
    }
    components.forEach((component, index) => {
      this._lintComponent(component, `${pathPrefix}[${index}]`);
    });
  }

  private _report(message: string, pathHint: string, line?: number) {
    this._issues.push({ message, pathHint, line });
  }

  private _lintComponent(raw: unknown, path: string): void {
    if (!isJsonObject(raw)) {
      this._report(`Component at "${path}" must be an object.`, path);
      return;
    }

    const discriminator = typeof raw.type === 'string' ? raw.type : '';
    if (!discriminator) {
      this._report(`Component at "${path}" is missing "type".`, path);
      return;
    }

    const descriptor = this._catalog.find((candidate) => candidate.discriminator === discriminator);
    if (!descriptor) {
      this._report(
        `Unknown component type "${discriminator}" at "${path}". Call list_component_types (MCP) or GET /component-types to see every registered discriminator.`,
        path,
        lineOfString(this._source, discriminator)
      );
      return;
    }

    this.lintProperties(raw, descriptor.properties, path);
    this._lintChildren(raw, descriptor.containment, path);
  }

  private _lintChildren(component: JsonObject, containment: ComponentContainment, path: string): void {
    const key = containment.propertyName;
    if (!key) {
      return;
    }

    if (containment.kind === 'ChildList') {
      this.lintList(component[key], `${path}.${key}`);
    } else if (containment.kind === 'NamedSections') {
      this._lintSections(component[key], `${path}.${key}`, containment.sectionChildrenPropertyName ?? 'children');
    } else if (containment.kind === 'KeyedChildren' && containment.keySourceProperty) {
      this._lintKeyedChildren(component, key, containment.keySourceProperty, path);
    }
  }

  private _lintSections(sections: unknown, sectionsPath: string, childrenKey: string): void {
    if (!Array.isArray(sections)) {
      return;
    }
    sections.forEach((section, index) => {
      if (isJsonObject(section)) {
        this.lintList(section[childrenKey], `${sectionsPath}[${index}].${childrenKey}`);
      }
    });
  }

  private _lintKeyedChildren(component: JsonObject, key: string, optionsKey: string, path: string): void {
    const byKey = component[key];
    if (!isJsonObject(byKey)) {
      return;
    }

    const declared = component[optionsKey];
    const options = Array.isArray(declared) ? declared.map(String) : [];
    for (const [optionKey, children] of Object.entries(byKey)) {
      if (!options.includes(optionKey)) {
        this._report(
          `"${optionKey}" is a key in "${path}.${key}" but not one of the values declared in "${path}.${optionsKey}" — this branch can never be shown.`,
          `${path}.${key}.${optionKey}`,
          lineOfString(this._source, optionKey)
        );
      }
      this.lintList(children, `${path}.${key}.${optionKey}`);
    }
  }

  lintProperties(component: JsonObject, properties: ComponentPropertyDescriptor[], path: string): void {
    for (const property of properties) {
      this._lintProperty(component[property.key], property, `${path}.${property.key}`);
    }
  }

  private _lintProperty(value: unknown, property: ComponentPropertyDescriptor, path: string): void {
    if (isMissing(value)) {
      if (property.required) {
        this._report(`"${property.title}" is required at "${path}" but is missing or empty.`, path);
      }
      return;
    }

    if (typeof value === 'string') {
      this._lintString(value, property, path);
    } else if (typeof value === 'number') {
      this._lintNumber(value, property, path);
    }
    this._lintNested(value, property, path);
  }

  private _lintNumber(value: number, property: ComponentPropertyDescriptor, path: string): void {
    if (property.minimum != null && value < property.minimum) {
      this._report(`"${property.title}" at "${path}" must be at least ${property.minimum}.`, path);
    }
    if (property.maximum != null && value > property.maximum) {
      this._report(`"${property.title}" at "${path}" must be at most ${property.maximum}.`, path);
    }
  }

  private _lintNested(value: unknown, property: ComponentPropertyDescriptor, path: string): void {
    if (property.valueKind === 'Array') {
      this._lintArrayItems(value, property.items?.properties, path);
    } else if (property.valueKind === 'Object') {
      this._lintObject(value, property.properties, path);
    }
  }

  private _lintArrayItems(items: unknown, properties: ComponentPropertyDescriptor[] | undefined, path: string): void {
    if (!Array.isArray(items) || !properties) {
      return;
    }
    items.forEach((item, index) => {
      this._lintObject(item, properties, `${path}[${index}]`);
    });
  }

  private _lintObject(value: unknown, properties: ComponentPropertyDescriptor[] | undefined, path: string): void {
    if (isJsonObject(value) && properties) {
      this.lintProperties(value, properties, path);
    }
  }

  private _lintString(value: string, property: ComponentPropertyDescriptor, path: string): void {
    const line = () => lineOfString(this._source, value);
    if (property.allowedValues?.length && !property.allowedValues.includes(value)) {
      this._report(
        `"${property.title}" at "${path}" is "${value}", which isn't one of: ${property.allowedValues.join(', ')}.`,
        path,
        line()
      );
    }
    this._lintReference(value, property, path);
    this._lintPattern(value, property, path);
    this._lintLength(value, property, path);
  }

  private _lintPattern(value: string, property: ComponentPropertyDescriptor, path: string): void {
    if (!property.pattern) {
      return;
    }
    try {
      if (!new RegExp(property.pattern).test(value)) {
        this._report(`"${property.title}" at "${path}" does not match the required pattern.`, path);
      }
    } catch {
      // An invalid regex is a descriptor-authoring bug, not something this document can fix.
    }
  }

  private _lintLength(value: string, property: ComponentPropertyDescriptor, path: string): void {
    if (property.minLength != null && value.length < property.minLength) {
      this._report(`"${property.title}" at "${path}" must be at least ${property.minLength} character(s) long.`, path);
    }
    if (property.maxLength != null && value.length > property.maxLength) {
      this._report(`"${property.title}" at "${path}" must be at most ${property.maxLength} character(s) long.`, path);
    }
  }

  /** Dangling-reference checks, mirroring ServiceBlueprint.ValidateFieldReferences/ValidateDataDisplayBindings server-side. */
  private _lintReference(value: string, property: ComponentPropertyDescriptor, path: string): void {
    const refs = this._refs;
    if (!refs) {
      return;
    }

    const problem = referenceProblem(value, property, refs);
    if (problem) {
      this._report(`"${property.title}" at "${path}" is "${value}", ${problem}`, path, lineOfString(this._source, value));
    }
  }
}

/** Why `value` does not resolve for a reference-typed property, or undefined if it does (or the property is not a reference). */
function referenceProblem(value: string, property: ComponentPropertyDescriptor, refs: ReferenceLintContext): string | undefined {
  switch (property.format) {
    case 'field-ref':
      return refs.siblingFieldKeys.has(value)
        ? undefined
        : "which isn't another field's fieldKey in this stage — visibility is only ever checked against the current stage's own submitted values, so this field would always stay hidden.";
    case 'calculation-ref':
      return calculationReferenceProblem(value, refs);
    case 'stage-ref':
      return refs.stageKeys.has(value) ? undefined : 'which is not a stage in this blueprint.';
    default:
      return undefined;
  }
}

/** A dotted name (user.email) reads into an object-valued field: the first segment must be a declared calculation field, and where that field declares a shape, the property too. */
function calculationReferenceProblem(value: string, refs: ReferenceLintContext): string | undefined {
  const [root, member] = value.split('.');
  if (!refs.calculationFieldNames.has(root)) {
    return "which is not a name declared in this blueprint's calculations.fields — it would never resolve.";
  }
  if (member !== undefined && refs.calculationShapes.get(root)?.has(member) === false) {
    return `but calculations.fields.${root}.shape declares no "${member}" property — it would never resolve.`;
  }
  return undefined;
}
