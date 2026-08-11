/**
 * @internal CodeMirror 6 setup for a single calculation-language expression field (a
 * calculations.fields entry, a series' from/to/values, a component's showWhen, a stage
 * validation's when/rule — anywhere this grammar is authored). Mirrors
 * wayfinder-definition-editor-codemirror.ts's own pattern exactly (own module, dynamically
 * imported so CM6 stays out of the main bundle until a tab that needs it is opened) but much
 * smaller: no line numbers, no search — expressions are always logically single-line, enforced
 * here via a transactionFilter that strips newlines.
 *
 * Highlighting is driven by the real tokenizer (`tokenize()`, exported from
 * wayfinder-calculations.js specifically for this) rather than a second hand-written one that
 * could drift from the actual grammar. Diagnostics come from the real parser
 * (`tryParseExpression`, calculation-runtime.ts) via the parser's own error position — a precise
 * squiggle at the exact bad character, not a generic pass/fail banner.
 *
 * Reference discovery is inline autocomplete (@codemirror/autocomplete), not a separate picker
 * widget — a prior version of this properties panel placed a filterable picker beside each
 * expression box, which broke down on a genuinely long expression (the box doesn't wrap — see
 * the transactionFilter below — so it simply overflowed past its column and visually collided
 * with the picker). Autocomplete needs no extra layout space at all: it opens a tooltip at the
 * cursor as the author types, so it scales the same way regardless of expression length or how
 * many fields/tables a blueprint has accumulated.
 */

import { EditorState } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { StreamLanguage, defaultHighlightStyle, syntaxHighlighting, type StreamParser } from '@codemirror/language';
import { linter, type Diagnostic } from '@codemirror/lint';
import {
  autocompletion,
  completionKeymap,
  startCompletion,
  type Completion,
  type CompletionContext,
  type CompletionResult,
} from '@codemirror/autocomplete';
import { tryParseExpression } from './calculation-runtime.js';
import { tokenize, type CalculationToken } from '../../../Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-calculations.js';

/** One insertable reference — a field/table/series name plus the human label to show alongside it. */
export interface ExpressionCompletionItem {
  /** The identifier actually inserted (and matched against as the author types). */
  name: string;
  /** Shown as the completion's "detail" text — the field's own label, or "table"/"field" for a non-input. */
  detail: string;
}

const KEYWORDS = new Set(['and', 'or', 'not', 'true', 'false']);
const FUNCTIONS = new Set(['if', 'min', 'max', 'clamp', 'abs', 'floor', 'round', 'pow', 'lookup']);
const PUNCTUATION = new Set(['(', ')', ',']);

interface CalcTokenizerState {
  tokens: CalculationToken[] | null;
  index: number;
}

function tagFor(token: CalculationToken): string | null {
  if (token.kind === 'number') return 'number';
  if (token.kind === 'string') return 'string';
  if (token.kind === 'op') return PUNCTUATION.has(token.value) ? 'punctuation' : 'operator';
  if (token.kind === 'identifier') {
    if (KEYWORDS.has(token.value)) return 'keyword';
    if (FUNCTIONS.has(token.value)) return 'builtin';
    return 'variableName';
  }
  return null;
}

const calcStreamParser: StreamParser<CalcTokenizerState> = {
  name: 'wayfinder-calculation-expression',
  startState: () => ({ tokens: null, index: 0 }),
  token(stream, state) {
    if (state.tokens === null) {
      try {
        state.tokens = tokenize(stream.string);
      } catch {
        state.tokens = [];
      }
      state.index = 0;
    }

    if (stream.eatSpace()) {
      return null;
    }

    const token = state.tokens[state.index];
    if (!token) {
      stream.skipToEnd();
      return null;
    }

    state.index += 1;
    stream.pos += token.value.length;
    return tagFor(token);
  },
};

const calcLanguage = StreamLanguage.define(calcStreamParser);

const calcLinter = linter(view => {
  const text = view.state.doc.toString();
  if (!text.trim()) {
    return [];
  }

  const result = tryParseExpression(text);
  if (result.ok) {
    return [];
  }

  const from = Math.min(result.position ?? 0, Math.max(text.length - 1, 0));
  const to = Math.min(from + 1, text.length);
  const diagnostic: Diagnostic = { from, to: Math.max(to, from), severity: 'error', message: result.message };
  return [diagnostic];
});

/** A single Enter/newline-producing keybinding stripped so the editor stays genuinely single-line. */
const singleLineFilter = EditorState.transactionFilter.of(tr =>
  tr.newDoc.lines > 1 ? [] : tr
);

/**
 * Reads the live item list via a getter (not a captured array) so completions stay correct as
 * the row's own available references change after this view was created — an author adding a
 * new field, renaming one, or adding a table must be reflected the next time completion opens,
 * not frozen at whatever existed when this specific expression editor first mounted.
 *
 * Filters against both the identifier (Completion.label — what CM6 matches by default) AND its
 * human-readable detail text, since a real author is just as likely to type a fragment of a
 * field's own label ("full", for a field labelled "Full name") as its exact fieldKey
 * ("applicantName") — the same forgiving match the old picker's combined "Label (fieldKey)"
 * display text gave for free. `filter: false` on the result tells CM6 not to additionally apply
 * its own default label-only filtering on top of this, which would otherwise hide a match found
 * only via `detail` (CM6 has no visibility into that field).
 */
function referenceCompletionSource(getItems: () => ExpressionCompletionItem[]) {
  return (context: CompletionContext): CompletionResult | null => {
    const word = context.matchBefore(/[\w.]*/);
    if (!word || (word.from === word.to && !context.explicit)) {
      return null;
    }

    const query = word.text.toLowerCase();
    const matches = getItems().filter(
      item => item.name.toLowerCase().includes(query) || item.detail.toLowerCase().includes(query)
    );
    if (matches.length === 0) {
      return null;
    }

    const options: Completion[] = matches.map(item => ({
      label: item.name,
      detail: item.detail,
      type: 'variable',
    }));

    return { from: word.from, options, filter: false };
  };
}

export interface CreateExpressionViewOptions {
  parent: HTMLElement;
  doc: string;
  readOnly: boolean;
  onChange: (value: string) => void;
  ariaLabel: string;
  /** Live source of insertable references for this editor — see referenceCompletionSource. */
  getCompletionItems?: () => ExpressionCompletionItem[];
}

export function createExpressionView({
  parent,
  doc,
  readOnly,
  onChange,
  ariaLabel,
  getCompletionItems,
}: CreateExpressionViewOptions): EditorView {
  const updateListener = EditorView.updateListener.of(update => {
    if (update.docChanged) {
      onChange(update.state.doc.toString());
    }
  });

  const state = EditorState.create({
    doc,
    extensions: [
      calcLanguage,
      syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
      calcLinter,
      singleLineFilter,
      autocompletion({ override: [referenceCompletionSource(getCompletionItems ?? (() => []))] }),
      keymap.of([{ key: 'Mod-Space', run: startCompletion }, ...completionKeymap, ...defaultKeymap, ...historyKeymap]),
      history(),
      EditorState.readOnly.of(readOnly),
      EditorView.contentAttributes.of({
        'aria-label': ariaLabel,
        'data-wayfinder-calculation-expression-input': 'true',
        'spellcheck': 'false',
      }),
      updateListener,
    ],
  });

  // Autocomplete tooltips are positioned/measured against this root — required for them to
  // render correctly (and be clickable) when the editor lives inside a shadow DOM, which every
  // instance of this component does (wayfinder-calculation-expression-editor is a LitElement).
  const root = (parent.getRootNode() as Document | ShadowRoot | null) ?? document;

  return new EditorView({ state, parent, root });
}
