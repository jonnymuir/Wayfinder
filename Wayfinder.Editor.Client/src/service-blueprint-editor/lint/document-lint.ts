import { EDITOR_TOP_LEVEL_FIELDS, matchesTopLevelFieldKind } from '../service-blueprint-canonical-json.js';
import type { Component, ComponentDescriptor } from '../types.js';
import { gatewayKindValues, stageKindValues } from '../types.js';
import { collectStageInputFields } from '../component-property-references.js';
import { ComponentLinter, type ReferenceLintContext } from './component-lint.js';
import { lintCalculations } from './calculation-lint.js';
import { type DefinitionLint, type JsonObject, isJsonObject, lineOfString } from './lint-support.js';

const ALLOWED_STAGE_KINDS: ReadonlySet<string> = new Set(stageKindValues);
const ALLOWED_GATEWAY_KINDS: ReadonlySet<string> = new Set(gatewayKindValues);

const isAbsentOrWellTyped = (value: unknown, kind: Parameters<typeof matchesTopLevelFieldKind>[1]) =>
  value === undefined || value === null || matchesTopLevelFieldKind(value, kind);

const isBlank = (value: unknown) => typeof value !== 'string' || !value.trim();

function describeJsonType(value: unknown): string {
  if (Array.isArray(value)) return 'an array';
  return typeof value === 'object' ? 'an object' : `a ${typeof value}`;
}

function describeExpectedKind(kind: string): string {
  const named: Record<string, string> = { boolean: 'true or false', array: 'an array', object: 'an object' };
  return named[kind] ?? `a ${kind}`;
}

/** Property names declared in each service field's `shape`. */
function shapesOf(fields: JsonObject): Map<string, Set<string>> {
  const shapes = new Map<string, Set<string>>();
  for (const [name, field] of Object.entries(fields)) {
    const shape = isJsonObject(field) ? field.shape : undefined;
    if (isJsonObject(shape)) {
      shapes.set(name, new Set(Object.keys(shape)));
    }
  }
  return shapes;
}

function stageKeysOf(root: JsonObject): Set<string> {
  const stages = Array.isArray(root.stages) ? root.stages : [];
  const keys = stages.map((stage) => (isJsonObject(stage) ? stage.stageKey : undefined));
  return new Set(keys.filter((key): key is string => typeof key === 'string' && key !== ''));
}

/** The blueprint-wide reference data the dangling-reference checks resolve against. */
function referenceContextFor(root: JsonObject): Omit<ReferenceLintContext, 'siblingFieldKeys'> {
  const calculations = isJsonObject(root.calculations) ? root.calculations : {};
  const fields = isJsonObject(calculations.fields) ? calculations.fields : {};
  return { calculationFieldNames: new Set(Object.keys(fields)), calculationShapes: shapesOf(fields), stageKeys: stageKeysOf(root) };
}

/** Checks a hand-edited Definition document, collecting every problem rather than stopping at the first. */
export class DocumentLinter {
  private readonly _issues: DefinitionLint[] = [];

  constructor(
    private readonly _source: string,
    private readonly _catalog: ComponentDescriptor[]
  ) {}

  lint(parsed: unknown): DefinitionLint[] {
    if (!isJsonObject(parsed)) {
      return [{ message: 'Definition must be a JSON object.' }];
    }

    this._lintRequiredStrings(parsed);
    this._lintTopLevelFieldKinds(parsed);
    this._lintUnknownKeys(parsed);
    if (!Array.isArray(parsed.queues)) {
      this._report('"queues" must be an array.', 'queues');
    }
    lintCalculations(parsed, this._source, this._catalog, this._issues);
    this._lintStages(parsed);
    this._lintGateways(parsed);
    return this._issues;
  }

  private _report(message: string, pathHint?: string, line?: number) {
    this._issues.push(pathHint === undefined && line === undefined ? { message } : { message, pathHint, line });
  }

  private _lintRequiredStrings(root: JsonObject) {
    for (const required of ['definitionKey', 'displayName', 'initialStage']) {
      if (isBlank(root[required])) {
        this._report(`Missing or empty "${required}".`, required, lineOfString(this._source, required));
      }
    }
  }

  /**
   * Typed once from the editor's own field table, so a wrong-typed value (allowManualRestart: "true")
   * is reported instead of being silently dropped when the definition is applied. Only reported when
   * the more specific check above has not already flagged the same property.
   */
  private _lintTopLevelFieldKinds(root: JsonObject) {
    for (const [key, kind] of Object.entries(EDITOR_TOP_LEVEL_FIELDS)) {
      const value = root[key];
      if (isAbsentOrWellTyped(value, kind) || this._issues.some((issue) => issue.pathHint === key)) {
        continue;
      }
      this._report(`"${key}" must be ${describeExpectedKind(kind)}, not ${describeJsonType(value)}.`, key, lineOfString(this._source, key));
    }
  }

  /** Properties the editor does not own are discarded on save, so say so rather than let a typo vanish without a word. */
  private _lintUnknownKeys(root: JsonObject) {
    for (const key of Object.keys(root).filter((candidate) => !(candidate in EDITOR_TOP_LEVEL_FIELDS))) {
      this._report(
        `"${key}" is not a property the editor keeps, so it would be discarded when this definition is saved. Check the spelling.`,
        key,
        lineOfString(this._source, key)
      );
    }
  }

  private _lintStages(root: JsonObject) {
    if (!Array.isArray(root.stages)) {
      this._report('"stages" must be an array.', 'stages');
      return;
    }

    const references = referenceContextFor(root);
    const seenKeys = new Set<string>();
    root.stages.forEach((stage, index) => {
      if (!isJsonObject(stage)) {
        this._report(`State at index ${index} must be an object.`);
        return;
      }
      const stageKey = typeof stage.stageKey === 'string' ? stage.stageKey : '';
      this._checkUniqueKey(stageKey, seenKeys, {
        missing: `State at index ${index} is missing "stageKey".`,
        duplicate: `Duplicate stage key "${stageKey}".`,
      });
      const label = stageKey || String(index);
      this._lintStageShape(stage, label);
      this._lintStageComponents(stage, index, references);
    });
  }

  private _lintStageShape(stage: JsonObject, label: string) {
    const kind = typeof stage.stageType === 'string' ? stage.stageType : '';
    this._checkKind(kind, ALLOWED_STAGE_KINDS, `State "${label}" has unsupported stageType "${kind}".`);
    this._checkQueueKey(stage, `State "${label}" is missing "queueKey".`);
    if (stage.routes !== undefined && !Array.isArray(stage.routes)) {
      this._report(`State "${label}" has a non-array "routes" value.`);
    }
  }

  /** Reports a missing or repeated key and returns how to refer to the node in later messages. */
  private _checkUniqueKey(key: string, seenKeys: Set<string>, messages: { missing: string; duplicate: string }): void {
    if (!key.trim()) {
      this._report(messages.missing);
    } else if (seenKeys.has(key)) {
      this._report(messages.duplicate, undefined, lineOfString(this._source, key));
    } else {
      seenKeys.add(key);
    }
  }

  private _checkKind(kind: string, allowed: ReadonlySet<string>, unsupported: string) {
    if (kind && !allowed.has(kind)) {
      this._report(`${unsupported} Allowed kinds: ${[...allowed].join(', ')}.`, undefined, lineOfString(this._source, kind));
    }
  }

  private _checkQueueKey(node: JsonObject, missing: string) {
    if (isBlank(node.queueKey)) {
      this._report(missing);
    }
  }

  private _lintStageComponents(stage: JsonObject, index: number, references: Omit<ReferenceLintContext, 'siblingFieldKeys'>) {
    if (this._catalog.length === 0 || stage.components === undefined) {
      return;
    }
    const siblingFieldKeys = new Set(
      collectStageInputFields(stage.components as Component[], this._catalog).map((field) => field.fieldKey)
    );
    new ComponentLinter(this._catalog, this._source, this._issues, { ...references, siblingFieldKeys }).lintList(
      stage.components,
      `stages[${index}].components`
    );
  }

  private _lintGateways(root: JsonObject) {
    if (!Array.isArray(root.gateways)) {
      this._report('"gateways" must be an array.', 'gateways');
      return;
    }

    const seenKeys = new Set<string>();
    root.gateways.forEach((gateway, index) => {
      if (!isJsonObject(gateway)) {
        this._report(`Gateway at index ${index} must be an object.`);
        return;
      }
      const key = typeof gateway.key === 'string' ? gateway.key : '';
      this._checkUniqueKey(key, seenKeys, {
        missing: `Gateway at index ${index} is missing "key".`,
        duplicate: `Duplicate gateway key "${key}".`,
      });
      const label = key || String(index);
      this._lintGatewayShape(gateway, label);
    });
  }

  private _lintGatewayShape(gateway: JsonObject, label: string) {
    const kind = typeof gateway.gatewayType === 'string' ? gateway.gatewayType : '';
    this._checkKind(kind, ALLOWED_GATEWAY_KINDS, `Gateway "${label}" has unsupported gatewayType "${kind}".`);
    this._checkQueueKey(gateway, `Gateway "${label}" is missing "queueKey".`);
    if (!Array.isArray(gateway.routes)) {
      this._report(`Gateway "${label}" must declare a "routes" array.`);
    }
  }
}
