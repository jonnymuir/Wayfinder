import { hydrateServiceBlueprintDefinition, type AuthoredServiceBlueprint } from '../types.js';
import { PLANNING_SERVICE_BLUEPRINT, cloneAuthoredServiceBlueprint } from '../fixtures/index.js';
import {
  GATEWAY_PILL_HEIGHT,
  GATEWAY_SIZE,
  LANE_HEADER_OFFSET,
  LANE_INSET,
  TOP_PADDING,
  computeTopology,
  computeServiceBlueprintGraphLayout,
  gatewayNodeId,
  laneForPosition,
  mergeLayout,
  parseGraphNodeId,
  rowBandCenter,
  stageNodeId,
} from './service-blueprint-graph-layout.js';
import { applyAutoArrange, pruneLayout, setNodePositions } from './service-blueprint-graph-layout-block.js';

type RawServiceBlueprint = Record<string, unknown>;

export type LayoutTestFixtures = {
  paymentDemo: RawServiceBlueprint;
  moneyModeller: RawServiceBlueprint;
};

// Approve/reject review loop: the Join's reject route targets the initial
// stage again, closing a cycle the layout must break with a backward edge.
// No current seed ships a Join loop-back, so the shape is authored inline.
const REVIEW_LOOP_SERVICE_BLUEPRINT: RawServiceBlueprint = {
  definitionKey: 'review-loop',
  displayName: 'Review Loop',
  version: 1,
  initialStage: 'draft',
  requestPolicy: 'single',
  queues: [
    { key: 'web-user', displayName: 'Applicant' },
    { key: 'admin', displayName: 'Reviewer' },
  ],
  stages: [
    {
      stateKey: 'draft',
      displayName: 'Draft',
      queueKey: 'web-user',
      routes: [{ id: 'draft--submit--submit-gw', target: 'submit-gw', trigger: 'submit' }],
    },
    {
      stateKey: 'review',
      displayName: 'Review',
      queueKey: 'admin',
      routes: [{ id: 'review--decide--decision-gw', target: 'decision-gw', trigger: 'decide' }],
    },
    { stateKey: 'done', displayName: 'Done', queueKey: 'web-user', routes: [] },
  ],
  gateways: [
    {
      key: 'submit-gw',
      displayName: 'Submit',
      gatewayType: 'Split',
      queueKey: 'web-user',
      routes: [{ id: 'submit-gw--submit--review', target: 'review', trigger: 'submit' }],
    },
    {
      key: 'decision-gw',
      displayName: 'Decision',
      gatewayType: 'Join',
      queueKey: 'admin',
      routes: [
        { id: 'decision-gw--approve--done', target: 'done', trigger: 'approve' },
        { id: 'decision-gw--reject--draft', target: 'draft', trigger: 'reject' },
      ],
    },
  ],
};

// Mirrors the juggling-licence shape reported on the canvas: a citizen-lane
// Split hands off to a caseworker-lane review stage, whose approve/reject
// routes merge into a citizen-lane Join. The Join's only direct predecessor
// (the caseworker stage) sits in a different lane, so it should climb back
// through it to the Split — its nearest same-lane ancestor — rather than
// being pulled toward, and clamped against the edge of, the caseworker lane.
const CROSS_LANE_MERGE_SERVICE_BLUEPRINT: RawServiceBlueprint = {
  definitionKey: 'cross-lane-merge',
  displayName: 'Cross-lane Merge',
  version: 1,
  initialStage: 'start',
  requestPolicy: 'single',
  queues: [
    { key: 'citizen', displayName: 'Applicant' },
    { key: 'caseworker', displayName: 'Caseworker' },
  ],
  stages: [
    {
      stateKey: 'start',
      displayName: 'Start',
      queueKey: 'citizen',
      routes: [{ id: 'start--submit--handoff', target: 'handoff', trigger: 'submit' }],
    },
    {
      stateKey: 'under-review',
      displayName: 'Under review',
      queueKey: 'caseworker',
      routes: [
        { id: 'under-review--approve--post-review', target: 'post-review', trigger: 'approve' },
        { id: 'under-review--reject--post-review', target: 'post-review', trigger: 'reject' },
      ],
    },
    { stateKey: 'approved', displayName: 'Approved', queueKey: 'citizen', routes: [] },
    { stateKey: 'rejected', displayName: 'Rejected', queueKey: 'citizen', routes: [] },
  ],
  gateways: [
    {
      key: 'handoff',
      displayName: 'Hand off to caseworker',
      gatewayType: 'Split',
      queueKey: 'citizen',
      routes: [{ id: 'handoff--continue--under-review', target: 'under-review', trigger: 'continue' }],
    },
    {
      key: 'post-review',
      displayName: 'Application under review',
      gatewayType: 'Join',
      queueKey: 'citizen',
      routes: [
        { id: 'post-review--approve--approved', target: 'approved', trigger: 'approve' },
        { id: 'post-review--reject--rejected', target: 'rejected', trigger: 'reject' },
      ],
    },
  ],
};

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function hydrate(raw: RawServiceBlueprint): AuthoredServiceBlueprint {
  return hydrateServiceBlueprintDefinition(JSON.parse(JSON.stringify(raw)) as AuthoredServiceBlueprint);
}

function assertCommonInvariants(name: string, serviceBlueprint: AuthoredServiceBlueprint, options: { strictRanks: boolean }) {
  // Queue labels resolve from the service blueprint's own queues; the standalone
  // availableQueues list is exercised by the host component, not here.
  const { topology, layout } = computeServiceBlueprintGraphLayout(serviceBlueprint, []);

  check(`${name}: every topology node gets a placement`,
    topology.nodes.every(node => layout.placements.has(node.id)),
    `${layout.placements.size}/${topology.nodes.length} placed`);

  check(`${name}: all placements are finite`,
    [...layout.placements.values()].every(p =>
      Number.isFinite(p.x) && Number.isFinite(p.y) && p.width > 0 && p.height > 0));

  check(`${name}: every node sits inside its lane band`,
    [...layout.placements.values()].every(p => {
      const lane = layout.lanes.find(candidate => candidate.key === p.queueKey);
      return lane !== undefined && p.x >= lane.x && p.x + p.width <= lane.x + lane.width;
    }));

  const laneXs = layout.lanes.map(lane => lane.x);
  check(`${name}: lanes are packed left-to-right without overlap`,
    laneXs.every((x, index) => index === 0
      || x >= layout.lanes[index - 1].x + layout.lanes[index - 1].width));

  const byQueueRank = new Map<string, { x: number; width: number }[]>();
  layout.placements.forEach(p => {
    const key = `${p.queueKey}#${p.rowRank}`;
    byQueueRank.set(key, [...(byQueueRank.get(key) ?? []), { x: p.x, width: p.width }]);
  });
  check(`${name}: same-band siblings never overlap horizontally`,
    [...byQueueRank.values()].every(items => {
      const sorted = [...items].sort((left, right) => left.x - right.x);
      return sorted.every((item, index) => index === 0
        || item.x >= sorted[index - 1].x + sorted[index - 1].width);
    }));

  check(`${name}: node Y follows its row band centre`,
    [...layout.placements.values()].every(p =>
      Math.abs((p.y + p.height / 2) - rowBandCenter(p.rowRank)) < 0.001));

  if (options.strictRanks) {
    check(`${name}: forward edges always flow to a strictly higher rank`,
      topology.edges.filter(edge => !edge.backward).every(edge => {
        const fromRank = topology.ranks.get(edge.fromId) ?? 0;
        const toRank = topology.ranks.get(edge.toId) ?? 0;
        return toRank > fromRank;
      }));
  }

  check(`${name}: bounds contain every placement`,
    [...layout.placements.values()].every(p =>
      p.x >= 0 && p.y >= 0
      && p.x + p.width <= layout.bounds.width
      && p.y + p.height <= layout.bounds.height));

  return { topology, layout };
}

export function run(fixtures: LayoutTestFixtures): number {
  failures = 0;

  // Planning: single-queue linear applicant flow.
  {
    const serviceBlueprint = cloneAuthoredServiceBlueprint(PLANNING_SERVICE_BLUEPRINT);
    const { topology, layout } = assertCommonInvariants('planning', serviceBlueprint, { strictRanks: true });

    check('planning: exactly one lane', layout.lanes.length === 1,
      `lanes: ${layout.lanes.map(lane => lane.key).join(', ')}`);

    const initial = layout.placements.get(stageNodeId(serviceBlueprint.initialStage));
    check('planning: initial state sits on the first row band',
      initial !== undefined && initial.y === TOP_PADDING + LANE_HEADER_OFFSET
        && initial.rowRank === 0);

    check('planning: no backward edges in a linear flow',
      topology.edges.every(edge => !edge.backward));
  }

  // Payment demo: two queues, Split + Join gateways.
  {
    const serviceBlueprint = hydrate(fixtures.paymentDemo);
    const { topology, layout } = assertCommonInvariants('payment-demo', serviceBlueprint, { strictRanks: true });

    check('payment-demo: two lanes in first-appearance order',
      layout.lanes.length === 2 && layout.lanes[0].key === 'web-user',
      `lanes: ${layout.lanes.map(lane => lane.key).join(', ')}`);

    const gatewayNodes = topology.nodes.filter(node => node.kind === 'gateway');
    check('payment-demo: both gateways are placed and ranked between their stages',
      gatewayNodes.length === 2 && gatewayNodes.every(node => layout.placements.has(node.id)));

    check('payment-demo: gateway sizes follow the pill/diamond rule',
      gatewayNodes.every(node => {
        const placement = layout.placements.get(node.id)!;
        const routeCount = (node.gateway.routes ?? []).length;
        return node.gateway.gatewayType === 'Split' && routeCount === 1
          ? placement.height === GATEWAY_PILL_HEIGHT
          : placement.height === GATEWAY_SIZE;
      }));

    check('payment-demo: every transition binding resolves to a hosting edge',
      topology.transitionBindings.every(binding => binding.edgeKey !== null
        && topology.edges.some(edge => edge.key === binding.edgeKey)));

    const lane = laneForPosition(layout.lanes, layout.lanes[1].x + 5);
    check('payment-demo: laneForPosition finds the containing lane',
      lane !== null && lane.key === layout.lanes[1].key);
  }

  // Review loop: the Join's reject route heads back upstream.
  {
    const serviceBlueprint = hydrate(REVIEW_LOOP_SERVICE_BLUEPRINT);
    const { topology } = assertCommonInvariants('review-loop', serviceBlueprint, { strictRanks: true });

    const backward = topology.edges.filter(edge => edge.backward);
    check('review-loop: the reject loop is flagged as a backward edge',
      backward.length === 1
        && backward[0].fromId === 'gateway:decision-gw'
        && backward[0].toId === 'stage:draft',
      `backward: ${backward.map(edge => edge.key).join(', ') || '(none)'}`);
    check('review-loop: backward edges point to an equal-or-lower rank',
      backward.every(edge =>
        (topology.ranks.get(edge.toId) ?? 0) <= (topology.ranks.get(edge.fromId) ?? 0)));
    check('review-loop: backward edges leave Join gateways only',
      backward.every(edge => parseGraphNodeId(edge.fromId).kind === 'gateway'));
    check('review-loop: the approve route still flows forward to done',
      topology.edges.some(edge => !edge.backward
        && edge.fromId === 'gateway:decision-gw' && edge.toId === 'stage:done'));
  }

  // Cross-lane merge: a Join gateway's direct predecessor lives in a
  // different lane than the Join itself — it should still centre under its
  // originating Split, not get pulled toward and clamped against the foreign
  // lane's edge.
  {
    const serviceBlueprint = hydrate(CROSS_LANE_MERGE_SERVICE_BLUEPRINT);
    const { layout } = assertCommonInvariants('cross-lane-merge', serviceBlueprint, { strictRanks: true });

    const split = layout.placements.get(gatewayNodeId('handoff'))!;
    const join = layout.placements.get(gatewayNodeId('post-review'))!;
    const splitCenter = split.x + split.width / 2;
    const joinCenter = join.x + join.width / 2;
    check('cross-lane-merge: Join gateway centres under its originating Split, not a foreign lane\'s stage',
      Math.abs(splitCenter - joinCenter) < 1,
      `split center ${splitCenter}, join center ${joinCenter}`);
  }

  // Money modeller: calculations block, recalculate self-loop, quote fan-out.
  // The self-loop cycles through a Split gateway, so ranks are only asserted
  // to be finite (matching the original canvas behaviour).
  {
    const serviceBlueprint = hydrate(fixtures.moneyModeller);
    const { topology } = assertCommonInvariants('money-modeller', serviceBlueprint, { strictRanks: false });

    check('money-modeller: every rank is a finite number',
      [...topology.ranks.values()].every(rank => Number.isFinite(rank)));
    check('money-modeller: all six stages and six gateways are in the topology',
      topology.nodes.filter(node => node.kind === 'stage').length === 6
      && topology.nodes.filter(node => node.kind === 'gateway').length === 6);
  }

  // Persisted layout: stored positions override the derived slots, lanes
  // stretch to cover dragged members, and helpers stay immutable.
  {
    const serviceBlueprint = hydrate(fixtures.paymentDemo);
    const topology = computeTopology(serviceBlueprint, []);
    const derived = mergeLayout(topology, undefined);
    const firstStageId = stageNodeId(serviceBlueprint.initialStage);
    const derivedPlacement = derived.placements.get(firstStageId)!;

    const draggedX = derivedPlacement.x + 400;
    const draggedY = derivedPlacement.y + 500;
    const moved = setNodePositions(serviceBlueprint, { [firstStageId]: { x: draggedX + 0.4, y: draggedY - 0.4 } });

    check('layout-block: setNodePositions stores rounded coordinates immutably',
      moved !== serviceBlueprint
      && serviceBlueprint.layout === undefined
      && moved.layout?.nodes?.[firstStageId]?.x === draggedX
      && moved.layout?.nodes?.[firstStageId]?.y === draggedY);

    const merged = mergeLayout(computeTopology(moved, []), moved.layout);
    const mergedPlacement = merged.placements.get(firstStageId)!;
    check('layout-block: mergeLayout applies the stored position',
      mergedPlacement.x === draggedX && mergedPlacement.y === draggedY);

    const lane = merged.lanes.find(candidate => candidate.key === mergedPlacement.queueKey)!;
    check('layout-block: the lane stretches to keep the dragged node inside its band',
      mergedPlacement.x >= lane.x + LANE_INSET - 0.001
      && mergedPlacement.x + mergedPlacement.width <= lane.x + lane.width - LANE_INSET + 0.001);

    check('layout-block: bounds grow with dragged content',
      merged.bounds.height >= draggedY + mergedPlacement.height + TOP_PADDING
      && merged.bounds.width >= lane.x + lane.width);

    check('layout-block: untouched nodes keep their derived slots',
      [...derived.placements.keys()]
        .filter(id => id !== firstStageId)
        .every(id => {
          const before = derived.placements.get(id)!;
          const after = merged.placements.get(id)!;
          return before.x === after.x && before.y === after.y;
        }));

    const pruned = pruneLayout({
      ...moved,
      stages: moved.stages.filter(stage => stage.stateKey !== serviceBlueprint.initialStage),
    });
    check('layout-block: pruneLayout drops entries for deleted nodes',
      pruned.layout === undefined);

    const arranged = applyAutoArrange(moved, []);
    const arrangedLayout = mergeLayout(computeTopology(arranged, []), arranged.layout);
    check('layout-block: applyAutoArrange writes explicit derived positions for every node',
      Object.keys(arranged.layout?.nodes ?? {}).length === topology.nodes.length
      && [...arrangedLayout.placements.values()].every(placement => {
        const stored = arranged.layout!.nodes![placement.id];
        return stored && stored.x === Math.round(derived.placements.get(placement.id)!.x)
          && stored.y === Math.round(derived.placements.get(placement.id)!.y);
      }));
  }

  // Empty serviceBlueprint: never throws, produces an empty single-lane-width canvas.
  {
    const { topology, layout } = computeServiceBlueprintGraphLayout(null, []);
    check('empty: no nodes, no lanes, non-zero bounds',
      topology.nodes.length === 0 && layout.lanes.length === 0
      && layout.bounds.width > 0 && layout.bounds.height > 0);
  }

  if (failures > 0) {
    console.error(`\n${failures} graph layout check(s) failed.`);
  } else {
    console.log('\nAll graph layout checks passed.');
  }
  return failures;
}
