import {
  mapServerDiagnosticsToIssues,
  normaliseServerSeverity,
  parseDiagnosticPath,
} from './server-diagnostic-location.js';
import type { AuthoredServiceBlueprint } from './types.js';
import type { ServiceBlueprintValidationOutcome } from './service-blueprint-source.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

const blueprint = { definitionKey: 'x', displayName: 'X', initialStage: 's', stages: [], gateways: [] } as unknown as AuthoredServiceBlueprint;

export function run(): number {
  failures = 0;

  // ── severity normalisation ──────────────────────────────────────────────
  check('severity "Error" → error', normaliseServerSeverity('Error') === 'error');
  check('severity "Warning" → warning', normaliseServerSeverity('Warning') === 'warning');
  check('severity 0 (STJ int) → error', normaliseServerSeverity(0) === 'error');
  check('severity 1 (STJ int) → warning', normaliseServerSeverity(1) === 'warning');

  // ── path → location ────────────────────────────────────────────────────
  check(
    'calculations.fields.{name} → calculation field location',
    JSON.stringify(parseDiagnosticPath('calculations.fields.totalCost', blueprint)) ===
      JSON.stringify({ kind: 'calculation', field: 'totalCost' })
  );
  check(
    'calculations.series.{name} → calculation series location',
    JSON.stringify(parseDiagnosticPath('calculations.series.trend', blueprint)) ===
      JSON.stringify({ kind: 'calculation', series: 'trend' })
  );
  check(
    'stages.{key}.actions[{n}] → stage action location',
    JSON.stringify(parseDiagnosticPath('stages.review.actions[2]', blueprint)) ===
      JSON.stringify({ kind: 'action', target: 'stage', stageKey: 'review', actionIndex: 2 })
  );
  check(
    'stages.{key}.routes[{n}] → containing stage location',
    JSON.stringify(parseDiagnosticPath('stages.review.routes[0]', blueprint)) ===
      JSON.stringify({ kind: 'stage', stageKey: 'review' })
  );
  check(
    'stages.{key}.components[{n}].showWhen → containing stage location',
    JSON.stringify(parseDiagnosticPath('stages.review.components[3].showWhen', blueprint)) ===
      JSON.stringify({ kind: 'stage', stageKey: 'review' })
  );
  check(
    'stages.{key} → stage location',
    JSON.stringify(parseDiagnosticPath('stages.declaration', blueprint)) ===
      JSON.stringify({ kind: 'stage', stageKey: 'declaration' })
  );
  check(
    'an unlocatable path (definitionKey) → non-jumpable document location',
    JSON.stringify(parseDiagnosticPath('definitionKey', blueprint)) === JSON.stringify({ kind: 'document' })
  );

  // ── outcome → issues ───────────────────────────────────────────────────
  {
    const outcome: ServiceBlueprintValidationOutcome = {
      isValid: false,
      diagnostics: [
        { code: 'CALC_FIELD_ERROR', path: 'calculations.fields.totalCost', message: 'boom', severity: 'Error' },
        { code: 'CALC_SERVICE_FIELD_UNVERIFIED', path: 'calculations.fields.member', message: 'cannot verify', severity: 'Warning' },
      ],
    };
    const issues = mapServerDiagnosticsToIssues(outcome, blueprint);
    check('every server diagnostic becomes one issue', issues.length === 2, JSON.stringify(issues));
    check('an Error diagnostic is blocking', issues[0].severity === 'error' && issues[0].blocking === true);
    check('a Warning diagnostic is non-blocking', issues[1].severity === 'warning' && issues[1].blocking === false);
    check('the server message is carried through verbatim', issues[0].message === 'boom');
    check('a calculations.* path lands on the calculation location', issues[0].location.kind === 'calculation');
    check('issue ids are stable and unique', issues[0].id !== issues[1].id && issues[0].id.includes('calculations.fields.totalCost'));
  }

  {
    const issues = mapServerDiagnosticsToIssues({ isValid: true, diagnostics: [] }, blueprint);
    check('a valid outcome produces no issues', issues.length === 0);
  }

  return failures;
}
