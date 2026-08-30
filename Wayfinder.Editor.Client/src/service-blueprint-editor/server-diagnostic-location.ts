/**
 * Adapts the host's authoritative validator output
 * (`Wayfinder.Engine.Services.ServiceBlueprintAuthoringService.Validate`, reached via
 * `ServiceBlueprintSource.validate`) into the validation rail's existing
 * `ServiceBlueprintValidationIssue` shape — so when a host provides `validate`, the rail renders
 * the *server's* diagnostics with the same badges, ordering and click-to-jump the in-browser
 * checks produce, and the two can never disagree.
 *
 * The only real work is turning a C# document `path` into a rail `location`. The C# grammar is
 * stable (see `Wayfinder/Models/ServiceDesign/ServiceBlueprint.cs` and
 * `ServiceBlueprintAuthoringService.cs`): `stages.{key}`, `stages.{key}.components[{n}]`,
 * `stages.{key}.actions[{n}]`, `stages.{key}.routes[{n}]`, `stages.{key}.validations[{n}].{field}`,
 * `calculations.fields.{name}`, `calculations.series.{name}`, plus a few unlocatable ones
 * (`definitionKey`, `params.*`).
 */

import type { AuthoredServiceBlueprint } from './types.js';
import type {
  ServiceBlueprintServerDiagnostic,
  ServiceBlueprintValidationOutcome,
} from './service-blueprint-source.js';
import type {
  ServiceBlueprintValidationIssue,
  ServiceBlueprintValidationLocation,
  ServiceBlueprintValidationSeverity,
} from './service-blueprint-validation.js';

/** `severity` arrives as the STJ string enum name or, from a host that serializes enums numerically, as 0/1. */
export function normaliseServerSeverity(
  severity: ServiceBlueprintServerDiagnostic['severity']
): ServiceBlueprintValidationSeverity {
  if (severity === 'Warning' || severity === 1) {
    return 'warning';
  }
  return 'error';
}

const CALC_FIELD = /^calculations\.fields\.(.+)$/;
const CALC_SERIES = /^calculations\.series\.(.+)$/;
const STAGE_ACTION = /^stages\.([^.[]+)\.actions\[(\d+)\]/;
const STAGE_ANY = /^stages\.([^.[]+)/;

/**
 * Best-effort map from a C# diagnostic `path` to a rail location the "jump to issue" handler
 * understands. Route/component/validation paths resolve to their containing stage (the reliable
 * jump target); anything unlocatable (`definitionKey`, `params.*`) becomes a non-jumpable
 * `document` location — the message is still listed.
 */
export function parseDiagnosticPath(
  path: string,
  _serviceBlueprint: AuthoredServiceBlueprint
): ServiceBlueprintValidationLocation {
  const calcField = CALC_FIELD.exec(path);
  if (calcField) {
    return { kind: 'calculation', field: calcField[1] };
  }

  const calcSeries = CALC_SERIES.exec(path);
  if (calcSeries) {
    return { kind: 'calculation', series: calcSeries[1] };
  }

  const stageAction = STAGE_ACTION.exec(path);
  if (stageAction) {
    return {
      kind: 'action',
      target: 'stage',
      stageKey: stageAction[1],
      actionIndex: Number(stageAction[2]),
    };
  }

  const stageAny = STAGE_ANY.exec(path);
  if (stageAny) {
    return { kind: 'stage', stageKey: stageAny[1] };
  }

  return { kind: 'document' };
}

function issueCodeFor(diagnostic: ServiceBlueprintServerDiagnostic): ServiceBlueprintValidationIssue['code'] {
  // The rail only uses `code` for its own grouping/telemetry; the authoritative machine-readable
  // kind is `diagnostic.code` (e.g. "CALC_FIELD_ERROR"), carried through in the message. Map the
  // calculation family to the existing calc codes so the Calculations tab styling still applies;
  // everything else is a generic server issue.
  if (diagnostic.path.startsWith('calculations.')) {
    return 'calculation-unknown-reference';
  }
  return 'server';
}

/**
 * Turn a server `ServiceBlueprintValidationOutcome` into the rail's issue list. Warnings are
 * non-blocking (they mean "couldn't verify statically", exactly as in the C# validator);
 * everything else blocks Save.
 */
export function mapServerDiagnosticsToIssues(
  outcome: ServiceBlueprintValidationOutcome,
  serviceBlueprint: AuthoredServiceBlueprint
): ServiceBlueprintValidationIssue[] {
  return (outcome.diagnostics ?? []).map((diagnostic, index) => {
    const severity = normaliseServerSeverity(diagnostic.severity);
    return {
      id: `server-${diagnostic.code}-${diagnostic.path}-${index}`,
      code: issueCodeFor(diagnostic),
      severity,
      blocking: severity === 'error',
      message: diagnostic.message,
      location: parseDiagnosticPath(diagnostic.path, serviceBlueprint),
    };
  });
}
