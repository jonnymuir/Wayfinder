import { applyNodeMoves, connectNodes, describeNodeMoves } from './graph-canvas-edits.js';
import { flattenRoutes } from '../route-model.js';
import { gatewayNodeId, stageNodeId } from './service-blueprint-graph-layout.js';
import type { ServiceBlueprint, StageDefinition } from '../types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function stage(stageKey: string, overrides: Partial<StageDefinition> = {}): StageDefinition {
  return { stageKey, displayName: stageKey.toUpperCase(), queueKey: 'citizen', components: [], ...overrides };
}

function blueprint(): ServiceBlueprint {
  return {
    definitionKey: 'bp',
    displayName: 'Blueprint',
    version: 1,
    initialStage: 'a',
    requestPolicy: 'single',
    stages: [stage('a'), stage('b'), stage('c')],
    gateways: [{ key: 'g', displayName: 'G', gatewayType: 'Split', queueKey: 'citizen', actor: 'citizen', roleGates: [], routes: [] }],
  };
}

export function run(): number {
  failures = 0;

  const moved = applyNodeMoves(blueprint(), [{ nodeId: stageNodeId('a'), x: 10, y: 20, queueKey: null }]);
  check('moving a node to the same lane leaves its queue alone', moved.stages[0].queueKey === 'citizen');

  const reassigned = applyNodeMoves(blueprint(), [{ nodeId: stageNodeId('b'), x: 0, y: 0, queueKey: 'reviewer' }]);
  check(
    'dropping a stage into another lane moves it to that queue',
    reassigned.stages.find((s) => s.stageKey === 'b')?.queueKey === 'reviewer'
  );
  check(
    'dropping a stage into another lane leaves the others alone',
    reassigned.stages.find((s) => s.stageKey === 'a')?.queueKey === 'citizen'
  );

  const gatewayMoved = applyNodeMoves(blueprint(), [{ nodeId: gatewayNodeId('g'), x: 0, y: 0, queueKey: 'reviewer' }]);
  check('dropping a gateway into another lane moves it to that queue', gatewayMoved.gateways?.[0].queueKey === 'reviewer');

  const connected = connectNodes(blueprint(), stageNodeId('a'), stageNodeId('b'));
  check(
    'connecting two stages produces a route',
    typeof connected === 'object' && connected !== null && flattenRoutes(connected.blueprint).some((r) => r.routeId === connected.routeId)
  );
  check(
    'connecting two stages routes through a Split gateway of the source',
    typeof connected === 'object' && connected !== null && (connected.blueprint.gateways?.length ?? 0) === 2
  );
  check('connecting a node to itself does nothing', connectNodes(blueprint(), stageNodeId('a'), stageNodeId('a')) === null);

  const viaGateway = connectNodes(blueprint(), gatewayNodeId('g'), stageNodeId('c'));
  check(
    'a gateway can route straight to a stage without adding a gateway',
    typeof viaGateway === 'object' && viaGateway !== null && viaGateway.blueprint.gateways?.length === 1
  );
  if (typeof viaGateway === 'object' && viaGateway !== null) {
    check(
      'connecting the same pair again is reported as a duplicate',
      connectNodes(viaGateway.blueprint, gatewayNodeId('g'), stageNodeId('c')) === 'duplicate'
    );
  }

  const label = (key: string) => key.toUpperCase();
  const queue = (key: string) => `Q-${key}`;
  check(
    'one moved node is announced by name',
    describeNodeMoves([{ nodeId: stageNodeId('a'), x: 0, y: 0, queueKey: null }], label, queue) === 'A moved.'
  );
  check(
    'several moved nodes are announced by count',
    describeNodeMoves(
      [
        { nodeId: stageNodeId('a'), x: 0, y: 0, queueKey: null },
        { nodeId: stageNodeId('b'), x: 0, y: 0, queueKey: null },
      ],
      label,
      queue
    ) === '2 nodes moved.'
  );
  check(
    'a lane change is announced with the new queue',
    describeNodeMoves([{ nodeId: stageNodeId('a'), x: 0, y: 0, queueKey: 'x' }], label, queue) === 'A moved to the Q-x queue.'
  );

  return failures;
}
