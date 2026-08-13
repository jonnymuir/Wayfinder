// Host integration EXAMPLE — not part of the editor's own public bundle
// surface. The reference MockBusinessApp uses this implementation to wire its
// `/mockapp/service-blueprints/*` endpoints into the editor's `ServiceBlueprintSource` contract.
// Real downstream apps fork/copy this file into their own bundle.

import {
  ServiceBlueprintSaveError,
  sanitiseServiceBlueprintSaveErrorLines,
  sanitiseServiceBlueprintSaveErrorText,
  type ServiceBlueprintSaveErrorDetail,
  type ServiceBlueprintSource,
  type ServiceBlueprintSummary,
} from '../service-blueprint-source.js';
import type { AuthoredServiceBlueprint } from '../types.js';
import { hydrateServiceBlueprintDefinition } from '../types.js';
import { serializeAuthoredServiceBlueprint } from '../service-blueprint-canonical-json.js';

type ProblemDetailsPayload = {
  title?: unknown;
  detail?: unknown;
  status?: unknown;
  traceId?: unknown;
  summary?: unknown;
  message?: unknown;
  errors?: unknown;
  extensions?: {
    traceId?: unknown;
    errors?: unknown;
  };
};

// The shape Wayfinder.Engine.Services.ServiceBlueprintSaveOutcome serializes to — returned by
// both /mockapp/service-blueprints/{key} and
// /wayfinder/service-blueprint-authoring/blueprints/{key} on EITHER a validation failure (400,
// Status "Invalid") or a version conflict (409, Status "Conflict"). Not a ProblemDetails payload,
// so it's parsed separately — see isServiceBlueprintSaveOutcomePayload/parseSaveOutcome below.
type ServiceBlueprintSaveOutcomePayload = {
  status?: unknown;
  diagnostics?: unknown;
  currentVersion?: unknown;
  newVersion?: unknown;
};

// The shape of each ServiceBlueprintDiagnostic in that array: Code, Path, Message, Severity —
// see Wayfinder/Models/ServiceDesign/ServiceBlueprintDiagnostic.cs.
type ServiceBlueprintDiagnosticPayload = {
  code?: unknown;
  path?: unknown;
  message?: unknown;
};

function isServiceBlueprintSaveOutcomePayload(payload: unknown): payload is ServiceBlueprintSaveOutcomePayload {
  return !!payload && typeof payload === 'object'
    && typeof (payload as ServiceBlueprintSaveOutcomePayload).status === 'string'
    && Array.isArray((payload as ServiceBlueprintSaveOutcomePayload).diagnostics);
}

/** `Path`s like `stages.review.validations[0].when` or `stages.review.components[2].showWhen`
 * name a real stage the editor can jump to — see ServiceBlueprintSaveErrorDetail's doc comment. */
function stageKeyFromDiagnosticPath(path: string): string | undefined {
  return /^stages\.([^.[]+)/.exec(path)?.[1];
}

function readSaveOutcomeDiagnosticDetails(diagnostics: unknown): ServiceBlueprintSaveErrorDetail[] {
  if (!Array.isArray(diagnostics)) {
    return [];
  }

  return diagnostics
    .filter((entry): entry is ServiceBlueprintDiagnosticPayload => !!entry && typeof entry === 'object')
    .flatMap(entry => {
      const message = sanitiseServiceBlueprintSaveErrorText(typeof entry.message === 'string' ? entry.message : null);
      if (!message) {
        return [];
      }

      const path = typeof entry.path === 'string' ? entry.path : '';
      return [{ message, stageKey: path ? stageKeyFromDiagnosticPath(path) : undefined }];
    });
}

function parseSaveOutcome(payload: ServiceBlueprintSaveOutcomePayload, statusCode: number, blueprintKey: string): ServiceBlueprintSaveError {
  const isConflict = statusCode === 409;
  const currentVersion = typeof payload.currentVersion === 'number' ? payload.currentVersion : null;
  const details = readSaveOutcomeDiagnosticDetails(payload.diagnostics);
  const detailLines = details.map(detail => detail.message);
  const summary = sanitiseServiceBlueprintSaveErrorText(detailLines[0])
    ?? (isConflict
      ? `“${blueprintKey}” was changed elsewhere since you loaded it${currentVersion != null ? ` (now at version ${currentVersion})` : ''}.`
      : `The host app rejected the save request for “${blueprintKey}”.`);

  return new ServiceBlueprintSaveError({
    title: isConflict ? 'This service blueprint changed elsewhere' : 'We couldn’t save this service blueprint',
    summary,
    details: details.filter(detail => detail.message !== summary),
    detailLines: detailLines.filter(line => line !== summary),
    statusCode,
    isConflict,
    currentVersion,
  });
}

function readStructuredErrorLines(value: unknown): string[] {
  if (Array.isArray(value)) {
    return sanitiseServiceBlueprintSaveErrorLines(value.filter((entry): entry is string => typeof entry === 'string'));
  }

  if (value && typeof value === 'object') {
    return Object.entries(value as Record<string, unknown>)
      .flatMap(([field, messages]) => {
        if (Array.isArray(messages)) {
          return messages
            .filter((message): message is string => typeof message === 'string')
            .map(message => field ? `${field}: ${message}` : message);
        }

        return typeof messages === 'string'
          ? [field ? `${field}: ${messages}` : messages]
          : [];
      });
  }

  return typeof value === 'string' ? sanitiseServiceBlueprintSaveErrorLines([value]) : [];
}

function parseProblemDetails(payload: ProblemDetailsPayload, statusCode: number, blueprintKey: string): ServiceBlueprintSaveError {
  const title = sanitiseServiceBlueprintSaveErrorText(typeof payload.title === 'string' ? payload.title : null)
    ?? 'We couldn’t save this service blueprint';
  const summary = sanitiseServiceBlueprintSaveErrorText(
    typeof payload.summary === 'string'
      ? payload.summary
      : typeof payload.detail === 'string'
        ? payload.detail
        : typeof payload.message === 'string'
          ? payload.message
          : null
  ) ?? `The host app rejected the save request for “${blueprintKey}”.`;
  const detailLines = sanitiseServiceBlueprintSaveErrorLines([
    ...readStructuredErrorLines(payload.errors),
    ...readStructuredErrorLines(payload.extensions?.errors),
  ]).filter(line => line !== summary);
  const traceId = sanitiseServiceBlueprintSaveErrorText(
    typeof payload.traceId === 'string'
      ? payload.traceId
      : typeof payload.extensions?.traceId === 'string'
        ? payload.extensions.traceId
        : null
  );

  return new ServiceBlueprintSaveError({
    title,
    summary,
    detailLines,
    traceId,
    statusCode,
  });
}

async function buildSaveError(response: Response, blueprintKey: string): Promise<ServiceBlueprintSaveError> {
  const payloadText = await response.text().catch(() => '');
  const contentType = response.headers.get('content-type') ?? '';
  const fallbackSummary = sanitiseServiceBlueprintSaveErrorText(payloadText)
    ?? `Save failed (${response.status} ${response.statusText}).`;

  if (contentType.includes('json') || payloadText.trim().startsWith('{')) {
    try {
      const payload = JSON.parse(payloadText) as unknown;
      if (isServiceBlueprintSaveOutcomePayload(payload)) {
        return parseSaveOutcome(payload, response.status, blueprintKey);
      }

      return parseProblemDetails(payload as ProblemDetailsPayload, response.status, blueprintKey);
    } catch {
      // Fall through to the plain-text fallback.
    }
  }

  return new ServiceBlueprintSaveError({
    title: 'We couldn’t save this service blueprint',
    summary: fallbackSummary,
    statusCode: response.status,
  });
}

export interface MockBusinessAppServiceBlueprintSourceOptions {
  /** Origin override for cross-origin development. Defaults to same-origin. */
  baseUrl?: string;
}

export class MockBusinessAppServiceBlueprintSource implements ServiceBlueprintSource {
  private readonly base: string;

  constructor(options: MockBusinessAppServiceBlueprintSourceOptions = {}) {
    this.base = (options.baseUrl ?? '').replace(/\/$/, '');
  }

  async list(): Promise<ServiceBlueprintSummary[]> {
    const response = await fetch(`${this.base}/mockapp/service-blueprints`, {
      headers: { Accept: 'application/json' },
      credentials: 'same-origin',
    });
    if (!response.ok) {
      throw new Error(`Failed to list service blueprints (${response.status} ${response.statusText}).`);
    }
    // /mockapp/service-blueprints serializes ServiceBlueprintSourceSummary(DefinitionKey, DisplayName) — there's no
    // separate "host-facing key" concept on this host, so blueprintKey and definitionKey are the
    // same string here. The naive `as ServiceBlueprintSummary[]` this replaced compiled fine (TypeScript
    // doesn't check across a JSON boundary) but left every option's `blueprintKey` undefined at
    // runtime, so the shell's `option.blueprintKey === this._draftBlueprintKey` selected-match never
    // fired and the <select> silently fell back to its first option regardless of which service blueprint
    // was actually loaded.
    const summaries = (await response.json()) as Array<{ definitionKey: string; displayName: string }>;
    return summaries.map(({ definitionKey, displayName }) => ({
      blueprintKey: definitionKey,
      definitionKey,
      displayName,
    }));
  }

  async load(blueprintKey: string): Promise<AuthoredServiceBlueprint> {
    const response = await fetch(`${this.base}/mockapp/service-blueprints/${encodeURIComponent(blueprintKey)}`, {
      headers: { Accept: 'application/json' },
      credentials: 'same-origin',
    });
    if (!response.ok) {
      throw new Error(`Failed to load service blueprint '${blueprintKey}' (${response.status} ${response.statusText}).`);
    }
    const payload = (await response.json()) as Record<string, unknown>;
    return hydrateServiceBlueprintDefinition(payload as unknown as AuthoredServiceBlueprint);
  }

  async save(blueprintKey: string, serviceBlueprint: AuthoredServiceBlueprint): Promise<void> {
    const body = serializeAuthoredServiceBlueprint(serviceBlueprint);
    const response = await fetch(`${this.base}/mockapp/service-blueprints/${encodeURIComponent(blueprintKey)}`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
      },
      credentials: 'same-origin',
      body,
    });
    if (!response.ok) {
      throw await buildSaveError(response, blueprintKey);
    }
  }

  /**
   * Cheap poll target: reads just the version, not the full definition. Uses the
   * definitionKey-keyed toolkit route rather than /mockapp/service-blueprints/* — both read from the
   * same underlying store, so either is correct, but this one exists specifically for this.
   */
  async checkVersion(blueprintKey: string): Promise<number | null> {
    const response = await fetch(
      `${this.base}/wayfinder/service-blueprint-authoring/blueprints/${encodeURIComponent(blueprintKey)}/version`,
      { headers: { Accept: 'application/json' }, credentials: 'same-origin' }
    );
    if (!response.ok) {
      return null;
    }
    const payload = (await response.json()) as { version?: unknown };
    return typeof payload.version === 'number' ? payload.version : null;
  }
}
