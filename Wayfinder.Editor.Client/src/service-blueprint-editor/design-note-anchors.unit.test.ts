import { anchorForSelection, anchorMatchesSelection, placeAnchor, targetForAnchor } from './design-note-anchors.js';
import type { ServiceBlueprint } from './types.js';

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
    initialStage: 'details',
    requestPolicy: 'single',
    stages: [
      {
        stageKey: 'details',
        displayName: 'Your details',
        queueKey: 'public',
        components: [{ type: 'text', fieldKey: 'email', label: 'Email address' }],
        routes: [{ id: 'to-decide', trigger: 'continue', target: 'decide' }],
      },
      { stageKey: 'check', displayName: 'Check your answers', queueKey: 'public', components: [], routes: [] },
    ],
    gateways: [
      {
        key: 'decide',
        displayName: 'Decide',
        gatewayType: 'Split',
        queueKey: 'public',
        routes: [{ id: '', trigger: 'continue', target: 'check' }],
      },
    ],
  };
}

export function run(): number {
  failures = 0;
  const bp = blueprint();

  {
    const place = placeAnchor(bp, { kind: 'Stage', key: 'check' });
    check('a stage anchor is labelled with the stage name', place.label === 'Stage: Check your answers', place.label);
    check('and belongs to that stage on the canvas', place.nodeId === 'stage:check');
  }

  {
    const place = placeAnchor(bp, { kind: 'Field', key: 'details', fieldKey: 'email' });
    check('a field anchor uses the field label', place.label === 'Field: Email address, on Your details', place.label);
    check('and belongs to its stage', place.nodeId === 'stage:details');
  }

  {
    const place = placeAnchor(bp, { kind: 'Route', key: 'to-decide' });
    check('a route anchor names its trigger and owner', place.label === 'Route: continue, from Your details', place.label);
  }

  {
    const place = placeAnchor(bp, { kind: 'Stage', key: 'gone' });
    check('a removed stage moves the note to the blueprint', place.removed && place.nodeId === null);
    check('and says what was removed', place.label === 'Whole blueprint (stage “gone” was removed)', place.label);
  }

  {
    const place = placeAnchor(bp, { kind: 'Field', key: 'details', fieldKey: 'phone' });
    check('a removed field keeps the note on its stage', place.removed && place.nodeId === 'stage:details', place.label);
  }

  {
    const anchor = anchorForSelection(bp, { kind: 'stage', stageKey: 'details' }, 0);
    check('a highlighted route with an id is what a new note is pinned to', anchor.kind === 'Route' && anchor.key === 'to-decide');
    const fallback = anchorForSelection(bp, { kind: 'gateway', gatewayKey: 'decide' }, 1);
    check('a route with no id pins the note to its owner instead', fallback.kind === 'Gateway' && fallback.key === 'decide');
    check('nothing selected pins it to the blueprint', anchorForSelection(bp, null, null).kind === 'Blueprint');
  }

  {
    const target = targetForAnchor(bp, { kind: 'Route', key: 'to-decide' });
    check(
      'showing a route anchor selects its owner and highlights the route',
      target?.selection?.kind === 'stage' && target.transitionIndex === 0,
      JSON.stringify(target)
    );
    check('a blueprint-level note has nowhere to go on the canvas', targetForAnchor(bp, { kind: 'Blueprint' }) === null);
  }

  {
    const selection = { kind: 'stage' as const, stageKey: 'details' };
    check(
      'a field note shows with its stage selected',
      anchorMatchesSelection(bp, { kind: 'Field', key: 'details', fieldKey: 'email' }, selection)
    );
    check('a note on another stage does not', !anchorMatchesSelection(bp, { kind: 'Stage', key: 'check' }, selection));
    check('a blueprint note shows when nothing is selected', anchorMatchesSelection(bp, { kind: 'Blueprint' }, null));
  }

  return failures;
}
