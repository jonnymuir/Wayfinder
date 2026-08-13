import { validateServiceBlueprint } from './service-blueprint-validation.js';
import type { AuthoredServiceBlueprint, AuthoredStage, AuthoredStageValidation } from './types.js';

let failures = 0;

function check(name: string, condition: boolean, detail?: string) {
  if (condition) {
    console.log(`  ok  ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL  ${name}${detail ? ` — ${detail}` : ''}`);
  }
}

function stage(overrides: Partial<AuthoredStage> = {}): AuthoredStage {
  return {
    stateKey: 'declaration',
    displayName: 'Declaration',
    kind: 'Question',
    ...overrides,
  };
}

function blueprint(stages: AuthoredStage[]): AuthoredServiceBlueprint {
  return {
    definitionKey: 'test-blueprint',
    displayName: 'Test blueprint',
    version: 1,
    initialStage: stages[0]?.stateKey ?? 'declaration',
    requestPolicy: 'multi-stage',
    stages,
  };
}

function validation(overrides: Partial<AuthoredStageValidation> = {}): AuthoredStageValidation {
  return {
    code: 'risk-mitigation-evidence-required',
    rule: 'true',
    message: 'Fix this before continuing.',
    ...overrides,
  };
}

export function run(): number {
  failures = 0;

  // ── A well-formed when/rule pair produces no stage-validation issues ───────
  {
    const issues = validateServiceBlueprint(
      blueprint([stage({ validations: [validation({ when: 'hasDangerousProps', rule: "riskAssessment <> '' or mitigationHasEvidence" })] })])
    );
    check(
      'valid when/rule expressions are not flagged',
      !issues.some(issue => issue.code === 'stage-validation-parse-error'),
      JSON.stringify(issues)
    );
  }

  // ── A malformed `when` blocks save, mirroring calculationValidationIssues ──
  {
    const issues = validateServiceBlueprint(
      blueprint([stage({ validations: [validation({ when: 'hasDangerousProps ko[ko[k[ok' })] })])
    );
    const issue = issues.find(candidate => candidate.code === 'stage-validation-parse-error');
    check('a malformed when expression is flagged', !!issue, JSON.stringify(issues));
    check('the flagged issue blocks save', issue?.blocking === true, JSON.stringify(issue));
    check(
      "the message names the rule code and surfaces the parser's own error",
      !!issue && issue.message.includes('risk-mitigation-evidence-required') && issue.message.includes("Unexpected character '['"),
      issue?.message
    );
  }

  // ── A malformed `rule` (the required field, no guard) is flagged the same way ──
  {
    const issues = validateServiceBlueprint(
      blueprint([stage({ validations: [validation({ rule: 'true ko[ko[k[ok' })] })])
    );
    check(
      'a malformed rule expression is flagged',
      issues.some(issue => issue.code === 'stage-validation-parse-error' && issue.message.includes('invalid rule expression')),
      JSON.stringify(issues)
    );
  }

  // ── An absent `when` (optional guard) is never flagged ──────────────────────
  {
    const issues = validateServiceBlueprint(
      blueprint([stage({ validations: [validation({ when: undefined })] })])
    );
    check('no `when` at all is not treated as a malformed expression', !issues.some(issue => issue.code === 'stage-validation-parse-error'), JSON.stringify(issues));
  }

  return failures;
}
