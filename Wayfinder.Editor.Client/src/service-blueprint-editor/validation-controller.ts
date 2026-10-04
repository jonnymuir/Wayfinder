import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { mapServerDiagnosticsToIssues } from './server-diagnostic-location.js';
import type { ServiceBlueprintSource } from './service-blueprint-source.js';
import { type ServiceBlueprintValidationIssue, validateServiceBlueprint } from './service-blueprint-validation.js';
import type { ActionCatalogEntry, ComponentDescriptor, ServiceBlueprint, SupportSystemDescriptor } from './types.js';

/** What validation needs to know about the editor right now. Read afresh on every run. */
export interface ValidationInputs {
  blueprint: ServiceBlueprint | null;
  blueprintKey: string;
  source: ServiceBlueprintSource | undefined;
  actionCatalog: readonly ActionCatalogEntry[];
  componentCatalog: readonly ComponentDescriptor[];
  supportSystemCatalog: readonly SupportSystemDescriptor[];
}

/** ~400ms after the last edit the rail refreshes; long enough that a fast typist isn't firing a validate per keystroke. */
const DEBOUNCE_MS = 400;

/**
 * Keeps the validation rail's issue list current. When the host's source can `validate`, that server call is
 * authoritative (it is exactly what Save enforces); otherwise, and if the call fails, the in-browser validator
 * stands in. A response that arrives after a newer run has started is discarded.
 */
export class ValidationController implements ReactiveController {
  issues: ServiceBlueprintValidationIssue[] = [];
  /** A server `validate` call is in flight. Save stays gated on the last result meanwhile. */
  pending = false;

  private debounce: number | null = null;
  private latestRequest = 0;

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: () => ValidationInputs
  ) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.cancelScheduled();
  }

  get blocking(): ServiceBlueprintValidationIssue[] {
    return this.issues.filter((issue) => issue.blocking);
  }

  get warnings(): ServiceBlueprintValidationIssue[] {
    return this.issues.filter((issue) => !issue.blocking);
  }

  get hasBlocking(): boolean {
    return this.issues.some((issue) => issue.blocking);
  }

  get summary(): string {
    if (!this.inputs().blueprint) {
      return 'Validation will appear when the service blueprint loads.';
    }
    if (this.pending && this.issues.length === 0) {
      return 'Checking the service blueprint…';
    }
    if (this.issues.length === 0) {
      return 'No validation issues. The service blueprint is ready to save.';
    }

    const blocking = this.blocking.length;
    const warnings = this.warnings.length;
    const parts: string[] = [];
    if (blocking > 0) {
      parts.push(`${blocking} blocking error${blocking === 1 ? '' : 's'}`);
    }
    if (warnings > 0) {
      parts.push(`${warnings} warning${warnings === 1 ? '' : 's'}`);
    }
    return `${parts.join(' and ')} in the validation rail.`;
  }

  /** Validate after the author pauses. */
  schedule(): void {
    if (typeof window === 'undefined') {
      void this.run();
      return;
    }
    this.cancelScheduled();
    this.debounce = window.setTimeout(() => {
      this.debounce = null;
      void this.run();
    }, DEBOUNCE_MS);
  }

  /** Validate now, dropping any scheduled run (first load, tests, and before saving). */
  async flush(): Promise<void> {
    this.cancelScheduled();
    await this.run();
  }

  private cancelScheduled(): void {
    if (this.debounce !== null && typeof window !== 'undefined') {
      window.clearTimeout(this.debounce);
    }
    this.debounce = null;
  }

  async run(): Promise<void> {
    const inputs = this.inputs();
    const blueprint = inputs.blueprint;
    if (!blueprint) {
      this.settle([], false);
      return;
    }

    const source = inputs.source;
    if (!source || typeof source.validate !== 'function') {
      this.settle(this.fallback(inputs, blueprint), false);
      return;
    }

    const request = ++this.latestRequest;
    this.pending = true;
    this.host.requestUpdate();
    try {
      const outcome = await source.validate(inputs.blueprintKey || blueprint.definitionKey, blueprint);
      if (request === this.latestRequest) {
        this.issues = mapServerDiagnosticsToIssues(outcome);
      }
    } catch {
      // The server is unreachable: never wedge the rail or gate Save on a stale blank; show the in-browser check instead.
      if (request === this.latestRequest) {
        this.issues = this.fallback(inputs, blueprint);
      }
    } finally {
      if (request === this.latestRequest) {
        this.pending = false;
        this.host.requestUpdate();
      }
    }
  }

  private fallback(inputs: ValidationInputs, blueprint: ServiceBlueprint): ServiceBlueprintValidationIssue[] {
    return validateServiceBlueprint(blueprint, [...inputs.actionCatalog], [...inputs.componentCatalog], [...inputs.supportSystemCatalog]);
  }

  private settle(issues: ServiceBlueprintValidationIssue[], pending: boolean): void {
    this.issues = issues;
    this.pending = pending;
    this.host.requestUpdate();
  }
}
