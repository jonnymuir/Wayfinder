import { validateServiceBlueprint } from './service-blueprint-validation.js';
import type { AuthoredAction, AuthoredServiceBlueprint, AuthoredStage, AuthoredStageValidation, SupportSystemDescriptor } from './types.js';

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

const TEXT_COMPONENT_CATALOG = [
  { discriminator: 'text', displayName: 'Text', category: 'Input' as const, clrType: 'TextInputComponent', isInput: true, properties: [], containment: { kind: 'None' as const } },
];

const SUPPORT_SYSTEM_KEY = 'safetynet-underwriting';
const CAPABILITY_KEY = 'validate-risk-assessment';

function supportSystemCatalog(): SupportSystemDescriptor[] {
  return [
    {
      key: SUPPORT_SYSTEM_KEY,
      displayName: 'SafetyNet Underwriting',
      capabilities: [
        {
          key: CAPABILITY_KEY,
          displayName: 'Validate a risk assessment',
          inputs: [
            { key: 'File', title: 'File', valueKind: 'String', required: true },
            { key: 'Notes', title: 'Notes', valueKind: 'String', required: false },
          ],
          outputs: [],
          supportedCompletionModes: ['Poll'],
          outcomes: [
            { key: 'approved', displayName: 'Approved' },
            { key: 'rejected', displayName: 'Rejected' },
          ],
        },
      ],
    },
  ];
}

function supportSystemCallAction(overrides: Partial<AuthoredAction> = {}): AuthoredAction {
  return {
    type: 'support-system-call',
    timing: 'OnEntry',
    params: { supportSystemKey: SUPPORT_SYSTEM_KEY, capabilityKey: CAPABILITY_KEY, inputs: { File: 'riskAssessment' } },
    ...overrides,
  };
}

function stageWithCapturedField(fieldKey: string): AuthoredStage {
  return stage({
    stateKey: 'upload',
    components: [{ type: 'text', fieldKey, label: fieldKey, required: false }],
  });
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

  // ── support-system-call: a well-formed action against a real captured field is not flagged ──
  {
    const stages = [
      stageWithCapturedField('riskAssessment'),
      stage({
        stateKey: 'automation',
        components: [],
        actions: [supportSystemCallAction()],
        routes: [{ id: 'r1', target: 'done', trigger: 'approved' }, { id: 'r2', target: 'done', trigger: 'rejected' }],
      }),
    ];
    const issues = validateServiceBlueprint(blueprint(stages), [], TEXT_COMPONENT_CATALOG, supportSystemCatalog());
    check('a valid support-system-call action is not flagged', !issues.some(issue => issue.code === 'action-support-system'), JSON.stringify(issues));
  }

  // ── support-system-call: missing supportSystemKey/capabilityKey is flagged ──
  {
    const stages = [stage({ stateKey: 'automation', actions: [supportSystemCallAction({ params: {} })] })];
    const issues = validateServiceBlueprint(blueprint(stages), [], [], supportSystemCatalog());
    check(
      'a support-system-call action with no keys set is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('missing-keys')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: an unregistered support system is flagged ──
  {
    const stages = [stage({
      stateKey: 'automation',
      actions: [supportSystemCallAction({ params: { supportSystemKey: 'not-registered', capabilityKey: CAPABILITY_KEY, inputs: {} } })],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], [], supportSystemCatalog());
    check(
      'an unregistered support system is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('unknown-support-system')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: an unregistered capability is flagged ──
  {
    const stages = [stage({
      stateKey: 'automation',
      actions: [supportSystemCallAction({ params: { supportSystemKey: SUPPORT_SYSTEM_KEY, capabilityKey: 'not-a-capability', inputs: {} } })],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], [], supportSystemCatalog());
    check(
      'an unregistered capability is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('unknown-capability')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: a missing required input is flagged ──
  {
    const stages = [stage({
      stateKey: 'automation',
      actions: [supportSystemCallAction({ params: { supportSystemKey: SUPPORT_SYSTEM_KEY, capabilityKey: CAPABILITY_KEY, inputs: {} } })],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], [], supportSystemCatalog());
    check(
      'a missing required input ("File") is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('missing-input-File')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: an input mapping key the capability never declared is flagged ──
  {
    const stages = [stageWithCapturedField('riskAssessment'), stage({
      stateKey: 'automation',
      actions: [supportSystemCallAction({ params: { supportSystemKey: SUPPORT_SYSTEM_KEY, capabilityKey: CAPABILITY_KEY, inputs: { File: 'riskAssessment', NotReal: 'riskAssessment' } } })],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], TEXT_COMPONENT_CATALOG, supportSystemCatalog());
    check(
      'an input key the capability never declared is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('unknown-input-NotReal')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: an input bound to a field that doesn't exist anywhere is flagged ──
  {
    const stages = [stage({
      stateKey: 'automation',
      actions: [supportSystemCallAction({ params: { supportSystemKey: SUPPORT_SYSTEM_KEY, capabilityKey: CAPABILITY_KEY, inputs: { File: 'notARealField' } } })],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], [], supportSystemCatalog());
    check(
      'an input bound to a nonexistent field is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.endsWith('input-unknown-field-File')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: an outgoing route trigger that isn't a declared outcome is flagged ──
  {
    const stages = [stageWithCapturedField('riskAssessment'), stage({
      stateKey: 'automation',
      components: [],
      actions: [supportSystemCallAction()],
      routes: [{ id: 'r1', target: 'done', trigger: 'maybe' }],
    })];
    const issues = validateServiceBlueprint(blueprint(stages), [], TEXT_COMPONENT_CATALOG, supportSystemCatalog());
    check(
      'a route trigger not among the capability’s declared outcomes is flagged',
      issues.some(issue => issue.code === 'action-support-system' && issue.id.includes('route-trigger-maybe')),
      JSON.stringify(issues)
    );
  }

  // ── support-system-call: server-side validation is stage-scoped only, so a route-level action isn't checked ──
  {
    const stages = [stage({ stateKey: 'a' }), stage({ stateKey: 'b' })];
    const bp = { ...blueprint(stages) };
    bp.stages[0].routes = [{ id: 'r1', target: 'b', trigger: 'continue', actions: [supportSystemCallAction({ params: {} })] }];
    const issues = validateServiceBlueprint(bp, [], [], supportSystemCatalog());
    check(
      'a route-level support-system-call action is not checked (mirrors ProcessManagerEngine’s stage-entry-only scope)',
      !issues.some(issue => issue.code === 'action-support-system'),
      JSON.stringify(issues)
    );
  }

  return failures;
}
