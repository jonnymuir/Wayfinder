import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { ExpressionCompletionItem } from './calculation-expression-editor-codemirror.js';

export type { ExpressionCompletionItem };

/**
 * @internal A single calculation-language expression field — the syntax-highlighted,
 * inline-diagnostic CodeMirror editor used throughout the Calculations tab (a field's own
 * expression, a series' from/to/values, a stage validation's when/rule). Loads CodeMirror 6
 * lazily on first connection, same as <wayfinder-definition-editor>, so authors who never open a
 * tab using this pay nothing for it. Reused per-row: each instance owns exactly one CM6 view, and
 * Lit's own rendering handles creating/destroying instances as rows are added/removed/reordered —
 * no manual instance bookkeeping needed in the parent.
 *
 * Autocomplete is inline IntelliSense for the whole calculation language, not just reference
 * lookup — see `completions` below for the blueprint-specific half (field/table/series names);
 * calculation-expression-editor-codemirror.ts always also offers the language's own fixed
 * vocabulary alongside it (every operator, boolean literal, and built-in function, with a
 * snippet template for a function's own arguments). Not a separate picker widget beside the box:
 * a prior version of this properties panel used exactly that, which broke down on a genuinely
 * long expression — this editor is deliberately single-line with no wrapping (see the
 * transactionFilter in calculation-expression-editor-codemirror.ts), so a long expression simply
 * overflowed past its column and visually collided with the picker sitting next to it.
 * Autocomplete needs no extra layout space — it opens as a tooltip at the cursor — so it scales
 * the same way regardless of expression length or how many references a blueprint has.
 *
 * Output event: `expression-input` { value: string } — fires after every user-visible change.
 * The host owns debounce/commit-point handling (blur/change), matching every other field in
 * this properties panel.
 */
@customElement('wayfinder-calculation-expression-editor')
export class WayfinderCalculationExpressionEditorElement extends LitElement {
  @property({ type: String })
  value = '';

  @property({ type: Boolean, attribute: 'read-only', reflect: true })
  readOnly = false;

  @property({ type: String, attribute: 'label-text' })
  ariaLabelText = 'Calculation expression';

  /** Insertable field/table/series names offered as the author types — see the class doc above. */
  @property({ attribute: false })
  completions: ExpressionCompletionItem[] = [];

  @state() private _ready = false;
  @state() private _loadError: string | null = null;

  private _view: import('@codemirror/view').EditorView | null = null;
  private _suppressInputEvent = false;

  connectedCallback() {
    super.connectedCallback();
    void this._loadEditor();
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    this._view?.destroy();
    this._view = null;
  }

  private async _loadEditor() {
    if (this._view || this._loadError) {
      return;
    }
    try {
      const { createExpressionView } = await import('./calculation-expression-editor-codemirror.js');
      this._ready = true;
      await this.updateComplete;
      const host = this.renderRoot?.querySelector?.('[data-wayfinder-calculation-expression-host]') as HTMLDivElement | null;
      if (!host || this._view) {
        return;
      }
      this._view = createExpressionView({
        parent: host,
        doc: this.value,
        readOnly: this.readOnly,
        ariaLabel: this.ariaLabelText,
        getCompletionItems: () => this.completions,
        onChange: next => {
          if (this._suppressInputEvent) {
            return;
          }
          this.value = next;
          this.dispatchEvent(
            new CustomEvent('expression-input', { detail: { value: next }, bubbles: true, composed: true })
          );
        },
      });
    } catch (err) {
      this._loadError = err instanceof Error ? err.message : String(err);
    }
  }

  updated(changed: Map<string, unknown>) {
    super.updated(changed);
    if (changed.has('value') && this._view) {
      const current = this._view.state.doc.toString();
      if (current !== this.value) {
        this._suppressInputEvent = true;
        try {
          this._view.dispatch({ changes: { from: 0, to: current.length, insert: this.value } });
        } finally {
          this._suppressInputEvent = false;
        }
      }
    }
  }

  focus(options?: FocusOptions) {
    if (this._view) {
      this._view.focus();
      return;
    }
    super.focus(options);
  }

  render() {
    return html`
      <div class="expression-host" data-wayfinder-calculation-expression-host></div>
      ${this._loadError
        ? html`<p class="load-error" role="alert">Couldn't load the expression editor: ${this._loadError}</p>`
        : !this._ready
          ? html`<p class="loading" role="status">Loading…</p>`
          : html`<p class="hint">Start typing, or press Ctrl+Space, for fields, functions, and keywords.</p>`}
    `;
  }

  static styles = css`
    :host {
      display: block;
    }

    .expression-host {
      border: 1px solid #cbd5e1;
      border-radius: 10px;
      background: #ffffff;
    }

    .expression-host:focus-within {
      outline: 3px solid #ffdd00;
      outline-offset: 0;
      border-color: transparent;
    }

    :host ::slotted(.cm-editor),
    .cm-editor {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
      font-size: 0.875rem;
      color: #0b0c0c;
      border-radius: 10px;
    }

    .cm-content {
      padding: 0.625rem 0.75rem !important;
    }

    .cm-diagnostic-error {
      border-bottom: 2px solid #b10e1e;
    }

    :host([read-only]) .expression-host {
      background: #f3f2f1;
    }

    .load-error {
      margin: 0.375rem 0 0;
      font-size: 0.8125rem;
      color: #b10e1e;
    }

    .loading {
      margin: 0;
      padding: 0.625rem 0.75rem;
      font-size: 0.875rem;
      color: #475569;
    }

    .hint {
      margin: 0.25rem 0 0;
      font-size: 0.75rem;
      color: #475569;
    }
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    'wayfinder-calculation-expression-editor': WayfinderCalculationExpressionEditorElement;
  }
}
