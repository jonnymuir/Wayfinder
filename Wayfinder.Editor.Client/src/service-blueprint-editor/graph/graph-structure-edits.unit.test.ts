import { insertGateway, insertStage, removeNode, routesTouching, uniqueStageKey, usedNodeKeys } from './graph-structure-edits.js';
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
  return { stageKey, displayName: stageKey, queueKey: 'citizen', components: [], ...overrides };
}

function blueprint(stages: StageDefinition[], overrides: Partial<ServiceBlueprint> = {}): ServiceBlueprint {
  return {
    definitionKey: 'bp',
    displayName: 'Blueprint',
    version: 1,
    initialStage: stages[0]?.stageKey ?? '',
    requestPolicy: 'single',
    stages,
    gateways: [],
    ...overrides,
  };
}

export function run(): number {
  failures = 0;
  const base = blueprint([
    stage('a', { routes: [{ id: 'r1', trigger: 'continue', target: 'b' }] }),
    stage('b', { routes: [{ id: 'r2', trigger: 'continue', target: 'c' }] }),
    stage('c'),
  ]);

  check('a new stage key is slugified from its name', uniqueStageKey(base, 'My New Stage!', 'x') === 'my-new-stage');
  check('a new stage key falls back when the name has no usable characters', uniqueStageKey(base, '!!!', 'new-stage') === 'new-stage');
  check('a taken stage key gets the next free numeric suffix', uniqueStageKey(base, 'a', 'x') === 'a-2');
  check(
    'stages and gateways share one key namespace',
    usedNodeKeys(
      insertGateway(base, { key: 'g', displayName: 'G', gatewayType: 'Split', queueKey: 'citizen', actor: 'citizen', roleGates: [] })
    ).has('g')
  );

  check(
    'append puts the stage last',
    insertStage(base, stage('z'), { position: 'append' })
      .stages.map((s) => s.stageKey)
      .join() === 'a,b,c,z'
  );
  check(
    'before places it ahead of the reference',
    insertStage(base, stage('z'), { position: 'before', referenceStageKey: 'b' })
      .stages.map((s) => s.stageKey)
      .join() === 'a,z,b,c'
  );
  check(
    'after places it behind the reference',
    insertStage(base, stage('z'), { position: 'after', referenceStageKey: 'b' })
      .stages.map((s) => s.stageKey)
      .join() === 'a,b,z,c'
  );
  check(
    'the first stage of an empty blueprint becomes the initial stage',
    insertStage(blueprint([]), stage('z'), { position: 'append' }).initialStage === 'z'
  );

  check('deleting reports the routes that touch the node', routesTouching(base, 'b').length === 2);

  const removed = removeNode(base, 'b');
  check('deleting a stage removes it', removed.stages.map((s) => s.stageKey).join() === 'a,c');
  check('deleting a stage removes routes that targeted it', removed.stages[0].routes?.length === 0);
  check('deleting the initial stage hands over to the first remaining stage', removeNode(base, 'a').initialStage === 'b');
  check('deleting another stage leaves the initial stage alone', removed.initialStage === 'a');

  return failures;
}
