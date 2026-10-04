import { html, nothing } from 'lit';
import type { ActionDefinition, SupportSystemCallActionParams, SupportSystemDescriptor } from '../types.js';
import { renderComponentPropertyFields, type ResolvedPropertyReferences } from '../component-property-editor.js';
import type { FieldReference } from '../component-property-references.js';
import { renderActionMessages } from './action-messages.js';
import type { ParamEditing } from './action-editor-host.js';

export function supportSystemCallParams(action: ActionDefinition): SupportSystemCallActionParams {
  const params = (action.params ?? {}) as SupportSystemCallActionParams;
  return {
    supportSystemKey: params.supportSystemKey ?? '',
    capabilityKey: params.capabilityKey ?? '',
    inputs: params.inputs ?? {},
  };
}

/** What is still missing or wrong in a support-system-call action, in the order the author fixes it. */
export function supportSystemCallMessages(params: SupportSystemCallActionParams, catalog: SupportSystemDescriptor[]): string[] {
  if (catalog.length === 0) {
    return ['No support systems are registered on this host — nothing to call yet.'];
  }
  if (!params.supportSystemKey) {
    return ['Choose a support system.'];
  }

  const supportSystem = catalog.find((candidate) => candidate.key === params.supportSystemKey);
  if (!supportSystem) {
    return [`“${params.supportSystemKey}” is not a registered support system.`];
  }
  if (!params.capabilityKey) {
    return ['Choose a capability.'];
  }

  const capability = supportSystem.capabilities.find((candidate) => candidate.key === params.capabilityKey);
  if (!capability) {
    return [`“${params.capabilityKey}” is not a capability of “${supportSystem.displayName}”.`];
  }

  return capability.inputs
    .filter((input) => input.required && !params.inputs?.[input.key])
    .map((input) => `“${input.title || input.key}” needs a field.`);
}

interface SupportSystemCallEditorInput {
  editing: ParamEditing;
  catalog: SupportSystemDescriptor[];
  fieldReferences: FieldReference[];
  index: number;
  action: ActionDefinition;
}

/**
 * The dedicated editor for a support-system-call action: pick a support system, then a capability
 * scoped to it (cascading — picking a different support system resets the capability and any bound
 * inputs), then one field per the chosen capability's own declared `inputs`. Not driven by the
 * generic `paramsSchema`-based renderer: that assumes one fixed schema per action `type`, which can't
 * express a schema that depends on a value (`capabilityKey`) chosen while authoring this same action.
 * Reuses `renderComponentPropertyFields` for the inputs themselves — a capability's `inputs` are
 * `ComponentPropertyDescriptor[]`, the exact shape a component's own properties use.
 */
export function renderSupportSystemCallEditor({ editing, catalog, fieldReferences, index, action }: SupportSystemCallEditorInput) {
  const params = supportSystemCallParams(action);
  const supportSystem = catalog.find((candidate) => candidate.key === params.supportSystemKey) ?? null;
  const capability = supportSystem?.capabilities.find((candidate) => candidate.key === params.capabilityKey) ?? null;
  const update = (patch: Partial<SupportSystemCallActionParams>) => editing.updateParams(index, { ...params, ...patch });

  // Field-ref rendering is reused by populating siblingFields with the blueprint-wide field list, not
  // the current stage's own: a capability input is typically bound to a field captured on an
  // *earlier* stage than the one carrying the action (see supportSystemFieldReferences).
  const references: ResolvedPropertyReferences = {
    siblingFields: fieldReferences,
    allFields: fieldReferences,
    stageOptions: [],
    calculationFieldNames: [],
  };

  return html`
    <div class="action-parameters support-system-call-editor" data-wayfinder-support-system-call-editor="${index}">
      <div class="field-grid">
        <label class="field-block" for="support-system-${index}">
          <span class="field-label">Support system</span>
          <select
            id="support-system-${index}"
            class="field-control"
            data-wayfinder-support-system-select="${index}"
            @change=${(event: Event) => update({ supportSystemKey: (event.currentTarget as HTMLSelectElement).value, capabilityKey: '', inputs: {} })}
          >
            <option value="" ?selected=${!params.supportSystemKey}>-- Choose a support system --</option>
            ${catalog.map((candidate) => html`<option value=${candidate.key} ?selected=${params.supportSystemKey === candidate.key}>${candidate.displayName}</option>`)}
          </select>
          ${supportSystem?.description ? html`<span class="field-help">${supportSystem.description}</span>` : nothing}
        </label>
        <label class="field-block" for="support-system-capability-${index}">
          <span class="field-label">Capability</span>
          <select
            id="support-system-capability-${index}"
            class="field-control"
            data-wayfinder-support-system-capability-select="${index}"
            ?disabled=${!supportSystem}
            @change=${(event: Event) => update({ capabilityKey: (event.currentTarget as HTMLSelectElement).value, inputs: {} })}
          >
            <option value="" ?selected=${!params.capabilityKey}>-- Choose a capability --</option>
            ${(supportSystem?.capabilities ?? []).map((candidate) => html`<option value=${candidate.key} ?selected=${params.capabilityKey === candidate.key}>${candidate.displayName}</option>`)}
          </select>
          ${capability?.description ? html`<span class="field-help">${capability.description}</span>` : nothing}
        </label>
      </div>
      ${
        capability
          ? html`
            <fieldset class="field-block field-block-full property-object">
              <legend class="field-label">Inputs</legend>
              ${renderComponentPropertyFields(capability.inputs, {
                value: params.inputs ?? {},
                onChange: (path, value) => update({ inputs: { ...(params.inputs ?? {}), [String(path[0])]: value as string } }),
                idPrefix: `support-system-call-${index}`,
                references,
              })}
            </fieldset>
            <p class="field-help">
              Outgoing routes from this stage should trigger on one of this capability's outcomes:
              ${capability.outcomes.map((outcome) => outcome.key).join(', ') || '(none declared)'}.
            </p>
          `
          : nothing
      }
      ${renderActionMessages(index, supportSystemCallMessages(params, catalog))}
    </div>
  `;
}
