import { BlueprintClipboard, type ClipboardContext } from './blueprint-clipboard.js';
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

function stage(stageKey: string, overrides: Partial<StageDefinition> = {}): StageDefinition {
  return { stageKey, displayName: stageKey.toUpperCase(), queueKey: 'citizen', components: [], ...overrides };
}

function blueprint(stages: StageDefinition[], gateways: ServiceBlueprint['gateways'] = []): ServiceBlueprint {
  return {
    definitionKey: 'bp',
    displayName: 'Blueprint',
    version: 1,
    initialStage: stages[0]?.stageKey ?? '',
    requestPolicy: 'single',
    stages,
    gateways,
  };
}

function context(overrides: Partial<ClipboardContext>): ClipboardContext {
  return {
    blueprint: null,
    selectedStageKey: null,
    multiSelection: [],
    selectedAction: null,
    actionCatalog: [],
    availableQueues: [],
    ...overrides,
  };
}

export function run(): number {
  failures = 0;

  {
    const clipboard = new BlueprintClipboard();
    const bp = blueprint([stage('a'), stage('b'), stage('c')]);
    check('nothing can be copied with nothing selected', !clipboard.canCopy(context({ blueprint: bp })));
    check('copy with nothing selected reports nothing copied', clipboard.copy(context({ blueprint: bp })) === null);
    check('paste with an empty clipboard does nothing', !clipboard.paste(context({ blueprint: bp })).ok);
    check('an empty clipboard says so', clipboard.summary.startsWith('Clipboard empty'));
  }

  {
    const clipboard = new BlueprintClipboard();
    const bp = blueprint([stage('a'), stage('b'), stage('c')]);
    const message = clipboard.copy(context({ blueprint: bp, selectedStageKey: 'a' }));
    check('copying the selected stage reports it', message === 'Copied stage A.', String(message));

    const outcome = clipboard.paste(context({ blueprint: bp, selectedStageKey: 'b' }));
    check('a stage can be pasted', outcome.ok);
    if (outcome.ok) {
      const keys = outcome.blueprint.stages.map((s) => s.stageKey);
      check(
        'the copy goes in after the selected stage with a fresh key',
        JSON.stringify(keys) === '["a","b","a-copy","c"]',
        JSON.stringify(keys)
      );
      check(
        'the pasted stage becomes the selection and the inspector is revealed',
        outcome.selectStageKey === 'a-copy' && outcome.revealInspector === true
      );
      check('the original blueprint is left untouched', bp.stages.length === 3);

      const again = clipboard.paste(context({ blueprint: outcome.blueprint, selectedStageKey: 'a' }));
      check(
        'pasting again picks the next unused key',
        again.ok && again.blueprint.stages.some((s) => s.stageKey === 'a-copy-2'),
        JSON.stringify(again)
      );
    }
  }

  {
    const clipboard = new BlueprintClipboard();
    const withAction = stage('a', { actions: [{ type: 'forms.load', timing: 'onEnter', summary: 'Load it' }] });
    const bp = blueprint([withAction, stage('b')]);
    const copied = clipboard.copy(context({ blueprint: bp, selectedAction: { action: withAction.actions![0], target: 'stage' } }));
    check('copying an action reports it by its summary', copied === 'Copied action Load it.', String(copied));

    const outcome = clipboard.paste(context({ blueprint: bp, selectedStageKey: 'b' }));
    check('an action can be pasted into the selected stage', outcome.ok);
    if (outcome.ok) {
      const pasted = outcome.blueprint.stages.find((s) => s.stageKey === 'b')?.actions ?? [];
      check('the action lands on that stage and is selected', pasted.length === 1 && outcome.selectActionIndex === 0);
    }
    check('an action cannot be pasted with no stage selected', !clipboard.paste(context({ blueprint: bp })).ok);
  }

  {
    const clipboard = new BlueprintClipboard();
    const bp = blueprint(
      [
        stage('a', { routes: [{ id: 'r1', target: 'g', trigger: 'go' }] }),
        stage('b', { routes: [{ id: 'r2', target: 'outside', trigger: 'go' }] }),
      ],
      [{ key: 'g', displayName: 'G', gatewayType: 'Split', queueKey: 'citizen', routes: [{ id: 'r3', target: 'b', trigger: 'go' }] }]
    );
    const message = clipboard.copy(context({ blueprint: bp, multiSelection: ['stage:a', 'stage:b', 'gateway:g'] }));
    check('a marquee selection copies stages and gateways together', message === 'Copied 2 stages and 1 gateway.', String(message));

    const outcome = clipboard.paste(context({ blueprint: bp }));
    check('a group can be pasted', outcome.ok);
    if (outcome.ok) {
      const copiedA = outcome.blueprint.stages.find((s) => s.stageKey === 'a-copy');
      const copiedB = outcome.blueprint.stages.find((s) => s.stageKey === 'b-copy');
      const copiedGateway = outcome.blueprint.gateways?.find((g) => g.key === 'g-copy');
      check(
        'routes between copied members follow the copies',
        copiedA?.routes?.[0].target === 'g-copy' && copiedGateway?.routes?.[0].target === 'b-copy'
      );
      check('routes leaving the group keep their original target', copiedB?.routes?.[0].target === 'outside');
      check('the first pasted stage becomes the selection', outcome.selectStageKey === 'a-copy');
    }
  }

  return failures;
}
