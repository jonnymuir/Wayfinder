import { isScalar, LineCounter, parseDocument, visit } from 'yaml';

export type DefinitionLint = {
  message: string;
  line?: number;
  pathHint?: string;
};

export type Mapping = Record<string, unknown>;

export function isMapping(value: unknown): value is Mapping {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

let indexed: { source: string; lines: Map<string, number> } | null = null;

/** The first line each scalar (key or value) appears on in the YAML source; cached for the last source read. */
function scalarLines(source: string): Map<string, number> {
  if (indexed?.source !== source) {
    const lineCounter = new LineCounter();
    const lines = new Map<string, number>();
    visit(parseDocument(source, { lineCounter }), {
      Scalar(_key, node) {
        const text = String(node.value);
        if (isScalar(node) && node.range && !lines.has(text)) {
          lines.set(text, lineCounter.linePos(node.range[0]).line);
        }
      },
    });
    indexed = { source, lines };
  }
  return indexed.lines;
}

/** The line a key or value first appears on in the YAML source, for pointing the author at it. */
export function lineOfString(source: string, value: string): number | undefined {
  return scalarLines(source).get(value);
}
