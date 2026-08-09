/**
 * @internal CodeMirror 6 setup for a single calculation-language expression field (a
 * calculations.fields entry, a series' from/to/values, a component's showWhen — anywhere this
 * grammar is authored). Mirrors wayfinder-definition-editor-codemirror.ts's own pattern exactly
 * (own module, dynamically imported so CM6 stays out of the main bundle until a tab that needs
 * it is opened) but much smaller: no line numbers, no search — expressions are always logically
 * single-line, enforced here via a transactionFilter that strips newlines.
 *
 * Highlighting is driven by the real tokenizer (`tokenize()`, exported from
 * wayfinder-calculations.js specifically for this) rather than a second hand-written one that
 * could drift from the actual grammar. Diagnostics come from the real parser
 * (`tryParseExpression`, calculation-runtime.ts) via the parser's own error position — a precise
 * squiggle at the exact bad character, not a generic pass/fail banner.
 */

import { EditorState } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { StreamLanguage, defaultHighlightStyle, syntaxHighlighting, type StreamParser } from '@codemirror/language';
import { linter, type Diagnostic } from '@codemirror/lint';
import { tryParseExpression } from './calculation-runtime.js';
import { tokenize, type CalculationToken } from '../../../Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-calculations.js';

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

export interface CreateExpressionViewOptions {
  parent: HTMLElement;
  doc: string;
  readOnly: boolean;
  onChange: (value: string) => void;
  ariaLabel: string;
}

export function createExpressionView({
  parent,
  doc,
  readOnly,
  onChange,
  ariaLabel,
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
      keymap.of([...defaultKeymap, ...historyKeymap]),
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

  return new EditorView({ state, parent });
}
