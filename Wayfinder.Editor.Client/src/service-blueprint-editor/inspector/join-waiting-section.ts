import { html, nothing } from 'lit';
import type { ServiceBlueprintGatewayDefinition, WaitingIcon, WaitingPace } from '../types.js';
import { waitingIconValues } from '../types.js';

type GatewayChange = (next: ServiceBlueprintGatewayDefinition, announcement: string) => void;

/** The usual lengths an author picks from, so the page can say "about a minute" rather than an arbitrary number. */
const usualWaits: ReadonlyArray<readonly [number, string]> = [
  [10, 'A few seconds'],
  [30, 'About 30 seconds'],
  [60, 'About a minute'],
  [120, 'About 2 minutes'],
  [300, 'About 5 minutes'],
  [600, 'About 10 minutes'],
];

const iconLabels: Record<WaitingIcon, string> = {
  Hourglass: 'Hourglass',
  Search: 'Magnifying glass',
  Document: 'Document',
  Butterfly: 'Butterfly',
  Hedgehog: 'Hedgehog',
};

const paceChoices: ReadonlyArray<{ pace: WaitingPace; label: string; help: string }> = [
  {
    pace: 'WhileTheyWait',
    label: 'While they wait',
    help: 'Seconds or minutes, usually an automated check. The page shows it is still working, and says so if it is taking longer than usual.',
  },
  {
    pace: 'Later',
    label: 'Later',
    help: 'Hours or days, usually a person or a slow process. The page says they can leave and come back, and never says it is taking longer than usual.',
  },
];

function choosePace(gateway: ServiceBlueprintGatewayDefinition, pace: WaitingPace, onChange: GatewayChange) {
  const label = paceChoices.find((choice) => choice.pace === pace)?.label ?? pace;
  onChange(
    {
      ...gateway,
      waitingPace: pace,
      waitingExpectedSeconds: pace === 'WhileTheyWait' ? gateway.waitingExpectedSeconds || 60 : undefined,
    },
    `${gateway.displayName} answer comes back: ${label.toLowerCase()}.`
  );
}

function renderUsualWait(gateway: ServiceBlueprintGatewayDefinition, onChange: GatewayChange) {
  const current = gateway.waitingExpectedSeconds || 60;
  const options = usualWaits.some(([seconds]) => seconds === current)
    ? usualWaits
    : [...usualWaits, [current, `About ${current} seconds`] as const].sort((a, b) => a[0] - b[0]);
  return html`
    <label class="field-block">
      <span class="field-label">How long does it usually take?</span>
      <select
        class="field-control"
        data-wayfinder-gateway-waiting-usual
        @change=${(event: Event) => {
          const seconds = Number((event.currentTarget as HTMLSelectElement).value);
          onChange({ ...gateway, waitingExpectedSeconds: seconds }, `${gateway.displayName} usual wait updated.`);
        }}
      >
        ${options.map(([seconds, label]) => html`<option value=${seconds} ?selected=${seconds === current}>${label}</option>`)}
      </select>
    </label>
  `;
}

function renderIcon(gateway: ServiceBlueprintGatewayDefinition, onChange: GatewayChange) {
  return html`
    <label class="field-block">
      <span class="field-label">Icon (optional)</span>
      <select
        class="field-control"
        data-wayfinder-gateway-waiting-icon
        @change=${(event: Event) => {
          const value = (event.currentTarget as HTMLSelectElement).value;
          const icon = waitingIconValues.find((candidate) => candidate === value);
          onChange({ ...gateway, waitingIcon: icon }, `${gateway.displayName} waiting icon ${icon ? 'set' : 'removed'}.`);
        }}
      >
        <option value="" ?selected=${!gateway.waitingIcon}>None</option>
        ${waitingIconValues.map((icon) => html`<option value=${icon} ?selected=${gateway.waitingIcon === icon}>${iconLabels[icon]}</option>`)}
      </select>
      <span class="field-help">Most waits need none. A small line drawing shown beside "Still working on it".</span>
    </label>
  `;
}

/** How a join's waiting page reads: the message, when the answer usually comes back, and an optional icon. */
export function renderJoinWaitingPace(gateway: ServiceBlueprintGatewayDefinition, onChange: GatewayChange) {
  return html`
    <fieldset class="field-block field-block-full waiting-pace" data-wayfinder-gateway-waiting-pace>
      <legend class="field-label">When does the answer usually come back?</legend>
      ${paceChoices.map(
        (choice) => html`
          <label class="checkbox-row waiting-pace-choice">
            <input
              type="radio"
              name="waiting-pace-${gateway.key}"
              value=${choice.pace}
              .checked=${gateway.waitingPace === choice.pace}
              @change=${() => choosePace(gateway, choice.pace, onChange)}
            />
            <span><strong>${choice.label}</strong><br /><span class="field-help">${choice.help}</span></span>
          </label>
        `
      )}
      ${gateway.waitingPace ? nothing : html`<span class="field-help">Not chosen yet, so the page stays neutral and makes no promise about time.</span>`}
    </fieldset>
    ${gateway.waitingPace === 'WhileTheyWait' ? renderUsualWait(gateway, onChange) : nothing}
    ${renderIcon(gateway, onChange)}
  `;
}
