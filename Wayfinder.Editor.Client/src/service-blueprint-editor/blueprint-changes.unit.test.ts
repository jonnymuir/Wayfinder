import { diffBlueprints, hasChanges, summariseChanges } from './blueprint-changes.js';
import type { ServiceBlueprint, StageDefinition } from './types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function blueprint(): ServiceBlueprint {
  return {
    definitionKey: 'licence',
    displayName: 'Apply for a licence',
    version: 3,
    initialStage: 'eligibility',
    requestPolicy: 'single',
    stages: [
      {
        stageKey: 'eligibility',
        displayName: 'Check eligibility',
        queueKey: 'public',
        components: [],
        routes: [{ id: 'to-check', trigger: 'continue', target: 'check' }],
      },
      { stageKey: 'check', displayName: 'Check your answers', queueKey: 'public', components: [], routes: [] },
    ],
    gateways: [],
    layout: { nodes: { eligibility: { x: 0, y: 0 } } },
  };
}

const evidence: StageDefinition = { stageKey: 'evidence', displayName: 'Upload evidence', queueKey: 'public', components: [] };

export function run(): number {
  failures = 0;

  {
    const before = blueprint();
    const after: ServiceBlueprint = { ...blueprint(), version: 4, layout: { nodes: { eligibility: { x: 90, y: 40 } } } };
    const changes = diffBlueprints(before, after);
    check('a new version that only moved things on the canvas has no changes', !hasChanges(changes));
    check('and says so', summariseChanges(changes) === 'only the layout changed', summariseChanges(changes));
  }

  {
    const after = blueprint();
    after.stages.push(evidence);
    const changes = diffBlueprints(blueprint(), after);
    check('a new stage is added', changes.added.map((node) => node.key).join() === 'evidence');
    check(
      'nothing else is reported',
      changes.changed.length === 0 && changes.removed.length === 0 && changes.addedRouteIndices.length === 0
    );
    check('the summary counts it', summariseChanges(changes) === '1 stage added', summariseChanges(changes));
  }

  {
    const after = blueprint();
    after.stages[1].displayName = 'Check your application';
    const changes = diffBlueprints(blueprint(), after);
    check('a renamed stage is changed', changes.changed.map((node) => node.key).join() === 'check');
  }

  {
    const after = blueprint();
    after.stages[0].routes = [{ id: 'to-check', trigger: 'continue', target: 'check', showWhen: 'eligible' }];
    const changes = diffBlueprints(blueprint(), after);
    check('a route given a condition is a changed route', changes.changedRouteIndices.join() === '0');
    check('its stage is not reported as changed as well', changes.changed.length === 0);
  }

  {
    const after = blueprint();
    after.stages.splice(1, 1);
    after.stages[0].routes = [];
    const changes = diffBlueprints(blueprint(), after);
    check('a removed stage keeps its name for the notice', changes.removed[0]?.displayName === 'Check your answers');
    check('its route is counted as removed', changes.removedRouteCount === 1);
    check('the summary lists both', summariseChanges(changes) === '1 stage removed, 1 route removed', summariseChanges(changes));
  }

  {
    const after: ServiceBlueprint = { ...blueprint(), tags: { team: 'licensing' } };
    after.gateways = [{ key: 'decide', displayName: 'Decide', gatewayType: 'Split', queueKey: 'public' }];
    const changes = diffBlueprints(blueprint(), after);
    check('a new gateway is added as a gateway', changes.added[0]?.kind === 'gateway');
    check('a change with nothing to draw is named', changes.otherChanges.join() === 'tags');
    check('the summary says both', summariseChanges(changes) === '1 gateway added, tags changed', summariseChanges(changes));
  }

  return failures;
}
