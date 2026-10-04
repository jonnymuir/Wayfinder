import { EditHistory } from './edit-history.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

export function run(): number {
  failures = 0;

  {
    const history = new EditHistory<string>();
    check('a fresh history has nothing to undo or redo', !history.canUndo && !history.canRedo);
    check('undo with nothing recorded does nothing', history.undo('now') === undefined);
  }

  {
    const history = new EditHistory<string>();
    history.record('a');
    history.record('b');
    check('undo returns the most recent state', history.undo('c') === 'b');
    check('undo steps back again', history.undo('b') === 'a');
    check('redo returns the state that was undone', history.redo('a') === 'b');
    check('redo steps forward again', history.redo('b') === 'c');
    check('nothing is left to redo', !history.canRedo);
  }

  {
    const history = new EditHistory<string>();
    history.record('a');
    history.undo('b');
    history.record('x');
    check('a new edit discards what could have been redone', !history.canRedo);
  }

  {
    const history = new EditHistory<number>(3);
    for (const n of [1, 2, 3, 4, 5]) history.record(n);
    const seen: number[] = [];
    let current = 6;
    for (let step = history.undo(current); step !== undefined; step = history.undo(current)) {
      seen.push(step);
      current = step;
    }
    check('only the most recent edits are kept, up to the limit', JSON.stringify(seen) === '[5,4,3]', JSON.stringify(seen));
  }

  {
    const history = new EditHistory<string>();
    check('the summary says nothing has changed yet', history.summary.startsWith('No editor changes yet'));
    history.record('a');
    check('the summary counts changes to undo', history.summary.startsWith('1 change available to undo'), history.summary);
    history.undo('b');
    check('the summary counts changes to redo', history.summary.includes('1 change available to redo'), history.summary);
    history.clear();
    check('clear forgets everything', !history.canUndo && !history.canRedo);
  }

  return failures;
}
