import { availableContexts, contextForTiming, timingForContext, updateActionSummary } from './action-editing.js';
import { computeServiceBlueprintGraphLayout, parseGraphNodeId } from './graph/service-blueprint-graph-layout.js';
import { newRouteId } from './route-model.js';
import type { QueueDefinition as AssignableQueue } from './stage-assignment.js';
import {
  type ActionCatalogEntry,
  type ActionDefinition,
  type NodePosition,
  type ServiceBlueprint,
  type ServiceBlueprintGatewayDefinition,
  type ServiceBlueprintRouteDefinition,
  type StageDefinition,
  serviceBlueprintGateways,
} from './types.js';

type ActionTarget = 'stage' | 'transition';

type ClipboardEntry =
  | { kind: 'stage'; stage: StageDefinition; label: string }
  | { kind: 'subgraph'; stages: StageDefinition[]; gateways: ServiceBlueprintGatewayDefinition[]; label: string }
  | { kind: 'action'; action: ActionDefinition; label: string };

/** What the author currently has selected, as far as copying and pasting care. */
export interface ClipboardContext {
  blueprint: ServiceBlueprint | null;
  /** The stage selected on the canvas, if any (a gateway selection is not a stage). */
  selectedStageKey: string | null;
  /** Prefixed node ids from the canvas marquee. */
  multiSelection: readonly string[];
  /** The action selected in the inspector, if any. */
  selectedAction: { action: ActionDefinition; target: ActionTarget } | null;
  actionCatalog: readonly ActionCatalogEntry[];
  availableQueues: readonly AssignableQueue[];
}

export type PasteOutcome =
  | {
      ok: true;
      blueprint: ServiceBlueprint;
      /** The stage to select afterwards, or null to keep the current selection. */
      selectStageKey: string | null;
      /** Whether the inspector should be brought into view (a single stage was pasted). */
      revealInspector?: boolean;
      /** The index of the pasted action in the selected stage, if an action was pasted. */
      selectActionIndex?: number;
      message: string;
    }
  | { ok: false; message?: string };

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

/**
 * The editor's clipboard: copying a stage, a group of stages and gateways, or an action out of a blueprint and pasting
 * it back in. It decides what can be copied and pasted and builds the resulting blueprint; it never mutates the
 * blueprint it is given and has no idea how the editor renders or records history.
 */
export class BlueprintClipboard {
  private entry: ClipboardEntry | null = null;

  get summary(): string {
    if (!this.entry) {
      return 'Clipboard empty — copy a stage or action to paste it elsewhere.';
    }
    return this.entry.kind === 'action'
      ? `Clipboard: action “${this.entry.label}” ready to paste.`
      : `Clipboard: stage “${this.entry.label}” ready to paste.`;
  }

  canCopy(context: ClipboardContext): boolean {
    return context.selectedAction !== null || context.selectedStageKey !== null || context.multiSelection.length >= 2;
  }

  canPaste(context: ClipboardContext): boolean {
    if (!context.blueprint || !this.entry) {
      return false;
    }
    return this.entry.kind === 'action' ? this.canPasteAction(this.entry.action, context) : true;
  }

  /** Copies the current selection. Returns what to tell the author, or null if there was nothing to copy. */
  copy(context: ClipboardContext): string | null {
    const { blueprint, selectedAction } = context;
    if (selectedAction) {
      const label =
        selectedAction.action.summary?.trim() ||
        context.actionCatalog.find((entry) => entry.type === selectedAction.action.type)?.label ||
        selectedAction.action.type;
      this.entry = { kind: 'action', action: clone(selectedAction.action), label };
      return `Copied action ${label}.`;
    }

    if (!blueprint) {
      return null;
    }

    const group = this.copyGroup(context, blueprint);
    if (group) {
      return group;
    }

    const stage = blueprint.stages.find((candidate) => candidate.stageKey === context.selectedStageKey);
    if (!stage) {
      return null;
    }
    this.entry = { kind: 'stage', stage: clone(stage), label: stage.displayName };
    return `Copied stage ${stage.displayName}.`;
  }

  paste(context: ClipboardContext): PasteOutcome {
    const { blueprint } = context;
    if (!blueprint || !this.entry) {
      return { ok: false };
    }

    switch (this.entry.kind) {
      case 'subgraph':
        return this.pasteGroup(this.entry, blueprint, context);
      case 'stage':
        return this.pasteStage(this.entry, blueprint, context.selectedStageKey);
      case 'action':
        return this.pasteAction(this.entry, blueprint, context);
    }
  }

  private copyGroup(context: ClipboardContext, blueprint: ServiceBlueprint): string | null {
    if (context.multiSelection.length < 2) {
      return null;
    }

    const selected = context.multiSelection.map(parseGraphNodeId);
    const stages = blueprint.stages.filter((stage) => selected.some((node) => node.kind === 'stage' && node.key === stage.stageKey));
    const gateways = serviceBlueprintGateways(blueprint).filter((gateway) =>
      selected.some((node) => node.kind === 'gateway' && node.key === gateway.key)
    );
    if (stages.length + gateways.length < 2) {
      return null;
    }

    const label = [
      stages.length > 0 ? `${stages.length} stage${stages.length === 1 ? '' : 's'}` : null,
      gateways.length > 0 ? `${gateways.length} gateway${gateways.length === 1 ? '' : 's'}` : null,
    ]
      .filter(Boolean)
      .join(' and ');
    this.entry = { kind: 'subgraph', stages: stages.map(clone), gateways: gateways.map(clone), label };
    return `Copied ${label}.`;
  }

  private pasteStage(
    entry: Extract<ClipboardEntry, { kind: 'stage' }>,
    blueprint: ServiceBlueprint,
    selectedStageKey: string | null
  ): PasteOutcome {
    const copied = clone(entry.stage);
    const stageKey = this.unusedKey(copied.stageKey, new Set(blueprint.stages.map((stage) => stage.stageKey)));
    const stages = [...blueprint.stages];
    const selectedIndex = selectedStageKey ? stages.findIndex((stage) => stage.stageKey === selectedStageKey) : -1;
    stages.splice(selectedIndex >= 0 ? selectedIndex + 1 : stages.length, 0, { ...copied, stageKey });

    return {
      ok: true,
      blueprint: { ...blueprint, stages },
      selectStageKey: stageKey,
      revealInspector: true,
      message: `Pasted stage ${copied.displayName}.`,
    };
  }

  private pasteAction(
    entry: Extract<ClipboardEntry, { kind: 'action' }>,
    blueprint: ServiceBlueprint,
    context: ClipboardContext
  ): PasteOutcome {
    if (!context.selectedStageKey) {
      return { ok: false };
    }

    const pasted = this.actionForStage(entry.action, context.actionCatalog);
    if (!pasted) {
      return { ok: false, message: `Action ${entry.label} cannot be pasted into the current stage.` };
    }

    const stageIndex = blueprint.stages.findIndex((stage) => stage.stageKey === context.selectedStageKey);
    if (stageIndex < 0) {
      return { ok: false };
    }

    const stages = [...blueprint.stages];
    const actions = [...(stages[stageIndex].actions ?? []), pasted];
    stages[stageIndex] = { ...stages[stageIndex], actions };
    return {
      ok: true,
      blueprint: { ...blueprint, stages },
      selectStageKey: null,
      selectActionIndex: actions.length - 1,
      message: `Pasted action ${entry.label} into ${stages[stageIndex].displayName}.`,
    };
  }

  /**
   * Every stage and gateway in the group gets a fresh unique key, routes between members of the group are remapped to
   * the new keys (routes leaving the group keep their original targets), and the copies are placed at a small offset
   * from their sources.
   */
  private pasteGroup(
    entry: Extract<ClipboardEntry, { kind: 'subgraph' }>,
    blueprint: ServiceBlueprint,
    context: ClipboardContext
  ): PasteOutcome {
    const usedKeys = new Set<string>([
      ...blueprint.stages.map((stage) => stage.stageKey),
      ...serviceBlueprintGateways(blueprint).map((gateway) => gateway.key),
    ]);
    const newKeys = new Map<string, string>();
    for (const key of [...entry.stages.map((stage) => stage.stageKey), ...entry.gateways.map((gateway) => gateway.key)]) {
      const fresh = this.unusedKey(key, usedKeys);
      usedKeys.add(fresh);
      newKeys.set(key, fresh);
    }

    const remapRoutes = (ownerKey: string, routes: ServiceBlueprintRouteDefinition[] | undefined): ServiceBlueprintRouteDefinition[] =>
      (routes ?? []).map((route) => {
        const target = newKeys.get(route.target) ?? route.target;
        return { ...route, target, id: newRouteId(ownerKey, route.trigger, target) };
      });

    const stages: StageDefinition[] = entry.stages.map((stage) => {
      const stageKey = newKeys.get(stage.stageKey) ?? stage.stageKey;
      return { ...clone(stage), stageKey, routes: remapRoutes(stageKey, stage.routes) };
    });
    const gateways: ServiceBlueprintGatewayDefinition[] = entry.gateways.map((gateway) => {
      const key = newKeys.get(gateway.key) ?? gateway.key;
      return { ...clone(gateway), key, routes: remapRoutes(key, gateway.routes) };
    });

    const nodes = this.offsetPlacements(entry, newKeys, blueprint, context);
    return {
      ok: true,
      blueprint: {
        ...blueprint,
        stages: [...blueprint.stages, ...stages],
        gateways: [...serviceBlueprintGateways(blueprint), ...gateways],
        layout: Object.keys(nodes).length > 0 ? { nodes } : blueprint.layout,
      },
      selectStageKey: stages[0]?.stageKey ?? null,
      message: `Pasted ${entry.label}.`,
    };
  }

  /** Where the pasted copies sit on the canvas: a small offset from where their sources are now. */
  private offsetPlacements(
    entry: Extract<ClipboardEntry, { kind: 'subgraph' }>,
    newKeys: ReadonlyMap<string, string>,
    blueprint: ServiceBlueprint,
    context: ClipboardContext
  ): Record<string, NodePosition> {
    const { layout } = computeServiceBlueprintGraphLayout(blueprint, [...context.availableQueues]);
    const nodes: Record<string, NodePosition> = { ...(blueprint.layout?.nodes ?? {}) };
    for (const [oldKey, newKey] of newKeys) {
      const kind = entry.stages.some((stage) => stage.stageKey === oldKey) ? 'stage' : 'gateway';
      const placement = layout.placements.get(`${kind}:${oldKey}`);
      if (placement) {
        nodes[`${kind}:${newKey}`] = { x: Math.round(placement.x + 48), y: Math.round(placement.y + 48) };
      }
    }
    return nodes;
  }

  private unusedKey(base: string, used: ReadonlySet<string>): string {
    let candidate = `${base}-copy`;
    for (let suffix = 2; used.has(candidate); suffix += 1) {
      candidate = `${base}-copy-${suffix}`;
    }
    return candidate;
  }

  private canPasteAction(action: ActionDefinition, context: ClipboardContext): boolean {
    if (!context.selectedStageKey) {
      return false;
    }
    const entry = context.actionCatalog.find((candidate) => candidate.type === action.type);
    return entry ? availableContexts(entry, 'stage').length > 0 : true;
  }

  /** The action as it should sit on a stage: timing and summary adjusted to what the catalog allows there; null if it cannot go on a stage. */
  private actionForStage(action: ActionDefinition, catalog: readonly ActionCatalogEntry[]): ActionDefinition | null {
    const copy = clone(action);
    const entry = catalog.find((candidate) => candidate.type === copy.type);
    if (!entry) {
      return { ...copy, timing: copy.timing === 'onExit' ? 'onExit' : 'onEnter' };
    }

    const contexts = availableContexts(entry, 'stage');
    if (contexts.length === 0) {
      return null;
    }
    const own = contextForTiming(copy.timing, 'stage');
    return updateActionSummary(entry, { ...copy, timing: timingForContext(contexts.includes(own) ? own : contexts[0]) });
  }
}
