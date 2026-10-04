import { SelectionController, selectionFromDetail, selectionsEqual } from './editor-selection.js';
import { ValidationNavigator } from './validation-navigation.js';
import { flattenRoutes } from './route-model.js';
import type { ServiceBlueprint, StageDefinition } from './types.js';
import type { ServiceBlueprintValidationIssue } from './service-blueprint-validation.js';

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

const blueprint: ServiceBlueprint = {
  definitionKey: 'bp',
  displayName: 'Blueprint',
  version: 1,
  initialStage: 'a',
  requestPolicy: 'single',
  stages: [stage('a', { routes: [{ id: 'r1', trigger: 'continue', target: 'b' }] }), stage('b')],
  gateways: [{ key: 'g', displayName: 'G', gatewayType: 'Split', queueKey: 'citizen', actor: 'citizen', roleGates: [], routes: [] }],
};

function setUp() {
  let revealed = 0;
  const host = { addController() {}, requestUpdate() {} };
  const selection = new SelectionController(host as never, { blueprint: () => blueprint, revealInspector: () => (revealed += 1) });
  return { selection, revealed: () => revealed };
}

export function run(): number {
  failures = 0;

  check(
    'a stage and a gateway with the same key are different selections',
    !selectionsEqual({ kind: 'stage', stageKey: 'x' }, { kind: 'gateway', gatewayKey: 'x' })
  );
  check('two empty selections are equal', selectionsEqual(null, null));
  check('an event naming a route yields no selection of its own', selectionFromDetail({ kind: 'transition', transitionIndex: 0 }) === null);

  {
    const { selection, revealed } = setUp();
    selection.apply({ kind: 'stage', stageKey: 'a' });
    check('selecting an existing stage selects it and opens the inspector', selection.stageKey === 'a' && revealed() === 1);
    selection.apply({ kind: 'stage', stageKey: 'missing' });
    check('selecting a stage that does not exist clears the selection', selection.current === null);
    selection.apply({ kind: 'gateway', gatewayKey: 'g' });
    check('a gateway can be selected', selection.gatewayKey === 'g' && selection.stageKey === null);
  }

  {
    const { selection } = setUp();
    selection.highlightTransition(0);
    check('highlighting a route selects the stage that owns it', selection.stageKey === 'a' && selection.transitionIndex === 0);
    selection.apply({ kind: 'stage', stageKey: 'b' });
    check('selecting something else drops the highlighted route', selection.transitionIndex === null);
  }

  {
    const { selection } = setUp();
    selection.apply({ kind: 'stage', stageKey: 'a' });
    selection.chooseAction({ target: 'stage', index: 2 });
    check('a chosen stage action is reported while its stage is selected', selection.stageActionIndex === 2);
    selection.apply({ kind: 'gateway', gatewayKey: 'g' });
    check('a stage action is not reported once a gateway is selected', selection.stageActionIndex === null);
  }

  {
    (globalThis as { requestAnimationFrame?: (callback: () => void) => number }).requestAnimationFrame = (callback) => {
      callback();
      return 0;
    };
    const { selection } = setUp();
    let tab = 'definition';
    const navigator = new ValidationNavigator({
      blueprint: () => blueprint,
      selection,
      showTab: (next) => {
        tab = next;
      },
      revealInspector: () => {},
      inspector: () => null,
    });
    const route = flattenRoutes(blueprint)[0];
    const issue = (location: ServiceBlueprintValidationIssue['location']) => ({ id: 'i', location }) as ServiceBlueprintValidationIssue;

    navigator.jump(issue({ kind: 'route', routeId: route.routeId }));
    check(
      'an issue about a route takes the author to that route on the canvas',
      tab === 'canvas' && selection.transitionIndex === 0 && selection.stageKey === 'a'
    );

    navigator.jump(issue({ kind: 'calculation' }));
    check('a calculation issue opens the Calculations tab', tab === 'calculations');

    navigator.jump(issue({ kind: 'document' }));
    check('an issue with nowhere to go leaves the view alone', tab === 'calculations');

    navigator.jump(issue({ kind: 'action', target: 'stage', stageKey: 'b', actionIndex: 1 }));
    check('an action issue selects its stage and the action', selection.stageKey === 'b' && selection.stageActionIndex === 1);
  }

  return failures;
}
