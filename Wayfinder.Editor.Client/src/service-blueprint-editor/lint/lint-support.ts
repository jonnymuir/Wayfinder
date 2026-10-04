export type DefinitionLint = {
  message: string;
  line?: number;
  pathHint?: string;
};

export type JsonObject = Record<string, unknown>;

export function isJsonObject(value: unknown): value is JsonObject {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

export function findLine(source: string, needle: string): number | undefined {
  const index = source.indexOf(needle);
  if (index < 0) {
    return undefined;
  }
  return source.slice(0, index).split('\n').length;
}

/** The line of a quoted JSON string in the source, for pointing the author at it. */
export function lineOfString(source: string, value: string): number | undefined {
  return findLine(source, `"${value}"`);
}
