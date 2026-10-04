import { type ReactiveController, type ReactiveControllerHost, type TemplateResult, html, nothing } from 'lit';
import { authoredServiceBlueprintJsonEquals, serializeAuthoredServiceBlueprint } from './service-blueprint-canonical-json.js';
import {
  type DefinitionLint,
  coerceParsedAuthoredServiceBlueprint,
  lintAuthoredServiceBlueprintDocument,
} from './service-blueprint-lint.js';
import type { ComponentDescriptor, ServiceBlueprint } from './types.js';

/** What the Definition tab needs from the editor: the blueprint to mirror, the catalog to lint against, and a way to apply an edit. */
export interface DefinitionInputs {
  blueprint: () => ServiceBlueprint | null;
  componentCatalog: () => readonly ComponentDescriptor[];
  /** Commit a blueprint parsed from the text as an ordinary edit (so it is undoable). */
  apply: (next: ServiceBlueprint) => void;
}

const APPLY_DEBOUNCE_MS = 250;

/**
 * The Definition tab: the blueprint as JSON text that stays in step with the visual editor in both directions. Text the
 * author types is parsed, linted and, once valid, applied as an edit; edits made visually rewrite the text. Invalid
 * text is kept (and explained) rather than thrown away, so a half-typed change is never lost.
 */
export class DefinitionController implements ReactiveController {
  text = '';
  parseError: string | null = null;
  schemaIssues: DefinitionLint[] = [];
  announcement = '';
  /** The JSON editor is a lazy chunk; it is fetched the first time the tab is opened. */
  editorLoaded = false;

  /** Canonical JSON of the blueprint when the text last matched it, so a visual edit is not echoed back as a text edit. */
  private lastCanonical = '';
  private debounce: number | null = null;

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: DefinitionInputs
  ) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.cancelDebounce();
  }

  get hasIssues(): boolean {
    return this.parseError !== null || this.schemaIssues.length > 0;
  }

  /** Line-anchored problems for the editor's gutter. */
  get diagnostics(): Array<{ line: number; severity: 'error' | 'warning'; message: string }> {
    const out: Array<{ line: number; severity: 'error' | 'warning'; message: string }> = [];
    if (this.parseError) {
      // JSON.parse errors often say "line N".
      const lineMatch = /line (\d+)/i.exec(this.parseError);
      out.push({ line: lineMatch ? Number(lineMatch[1]) : 1, severity: 'error', message: this.parseError });
    }
    for (const issue of this.schemaIssues) {
      if (issue.line) {
        out.push({ line: issue.line, severity: 'error', message: issue.message });
      }
    }
    return out;
  }

  async ensureEditorLoaded(): Promise<void> {
    if (this.editorLoaded) {
      return;
    }
    await import('./wayfinder-definition-editor.js');
    this.editorLoaded = true;
    this.host.requestUpdate();
  }

  /** A new blueprint was loaded: forget the old text's problems and what it last matched. */
  reset(): void {
    this.lastCanonical = '';
    this.parseError = null;
    this.schemaIssues = [];
  }

  /** Mirror the blueprint into the text when it changed visually. Call after every host update. */
  syncFromBlueprint(): void {
    const blueprint = this.inputs.blueprint();
    if (!blueprint) {
      const changed = this.text !== '' || this.hasIssues;
      this.text = '';
      this.parseError = null;
      this.schemaIssues = [];
      this.lastCanonical = '';
      if (changed) {
        this.host.requestUpdate();
      }
      return;
    }
    const canonical = serializeAuthoredServiceBlueprint(blueprint);
    if (canonical === this.lastCanonical) {
      return;
    }
    this.text = canonical;
    this.lastCanonical = canonical;
    this.parseError = null;
    this.schemaIssues = [];
    this.host.requestUpdate();
  }

  /** The component catalog arrived after the text was loaded: lint what is already there. */
  relint(): void {
    if (this.text) {
      this.applyText();
    }
  }

  /** Tests and the host: apply any pending text now instead of after the debounce. */
  flush(): void {
    this.cancelDebounce();
    this.applyText();
  }

  private onInput = (event: CustomEvent<{ value: string }>) => {
    this.text = event.detail.value;
    this.cancelDebounce();
    this.debounce = window.setTimeout(() => {
      this.debounce = null;
      this.applyText();
    }, APPLY_DEBOUNCE_MS);
    this.host.requestUpdate();
  };

  private revert = () => {
    const blueprint = this.inputs.blueprint();
    if (!blueprint) {
      return;
    }
    this.cancelDebounce();
    this.text = serializeAuthoredServiceBlueprint(blueprint);
    this.lastCanonical = this.text;
    this.parseError = null;
    this.schemaIssues = [];
    this.announce('Definition reverted to the current service blueprint.');
    this.host.requestUpdate();
  };

  private applyText(): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(this.text);
    } catch (err) {
      this.parseError = err instanceof Error ? err.message : String(err);
      this.schemaIssues = [];
      this.host.requestUpdate();
      return;
    }

    const issues = lintAuthoredServiceBlueprintDocument(parsed, this.text, [...this.inputs.componentCatalog()]);
    if (issues.length > 0) {
      this.parseError = null;
      this.schemaIssues = issues;
      this.host.requestUpdate();
      return;
    }

    const next = coerceParsedAuthoredServiceBlueprint(parsed);
    this.parseError = null;
    this.schemaIssues = [];
    // Mark the canonical so the blueprint -> text sync does not echo this back.
    this.lastCanonical = serializeAuthoredServiceBlueprint(next);
    this.host.requestUpdate();

    if (authoredServiceBlueprintJsonEquals(this.inputs.blueprint(), next)) {
      return;
    }

    this.inputs.apply(next);
    const stages = next.stages.length;
    const gateways = next.gateways?.length ?? 0;
    this.announce(
      `Definition updated. ${stages} ${stages === 1 ? 'stage' : 'stages'}, ${gateways} ${gateways === 1 ? 'gateway' : 'gateways'}.`
    );
  }

  private announce(message: string): void {
    this.announcement = '';
    requestAnimationFrame(() => {
      this.announcement = message;
      this.host.requestUpdate();
    });
  }

  private cancelDebounce(): void {
    if (this.debounce !== null && typeof window !== 'undefined') {
      window.clearTimeout(this.debounce);
    }
    this.debounce = null;
  }

  renderPanel(): TemplateResult {
    if (!this.inputs.blueprint()) {
      return html`<div class="definition-empty" data-wayfinder-definition-empty>
        Loading the service blueprint definition…
      </div>`;
    }

    return html`
      <div class="definition-panel" data-wayfinder-definition-panel>
        ${this.renderBanner()}
        <div class="definition-editor-frame">
          ${
            this.editorLoaded
              ? html`
                <wayfinder-definition-editor
                  data-wayfinder-definition-editor
                  .value=${this.text}
                  .diagnostics=${this.diagnostics}
                  @definition-input=${this.onInput}
                ></wayfinder-definition-editor>
              `
              : html`<p class="definition-loading" role="status" data-wayfinder-definition-tab-loading>
                Preparing the JSON editor…
              </p>`
          }
        </div>
        <div class="sr-only" role="status" aria-live="polite" data-wayfinder-definition-announcement>
          ${this.announcement}
        </div>
      </div>
    `;
  }

  private renderBanner() {
    if (!this.hasIssues) {
      return nothing;
    }
    const summary = this.parseError
      ? `JSON is not valid: ${this.parseError}`
      : (this.schemaIssues[0]?.message ?? 'Definition does not match the service blueprint schema.');
    const additional =
      !this.parseError && this.schemaIssues.length > 1
        ? html`<ul class="definition-banner-list">
          ${this.schemaIssues.slice(1, 5).map((issue) => html`<li>${issue.message}</li>`)}
        </ul>`
        : nothing;

    return html`
      <div class="definition-banner" role="alert" data-wayfinder-definition-banner>
        <p class="definition-banner-summary">
          <strong>Definition can't be applied:</strong> ${summary}
        </p>
        ${additional}
        <div class="definition-banner-actions">
          <button type="button" class="govuk-button" data-wayfinder-definition-apply disabled aria-disabled="true">
            Apply when valid
          </button>
          <button
            type="button"
            class="govuk-button govuk-button--secondary"
            data-wayfinder-definition-revert
            @click=${this.revert}
          >
            Revert to current
          </button>
        </div>
      </div>
    `;
  }
}
