import { type TemplateResult, html, nothing } from 'lit';

/** The "fix these before saving" list an action shows under its parameters. */
export function renderActionMessages(index: number, messages: string[]): TemplateResult | typeof nothing {
  if (messages.length === 0) {
    return nothing;
  }
  return html`
    <div class="action-validation" data-wayfinder-action-errors="${index}">
      <p class="action-validation-title">Fix these action details before saving:</p>
      <ul>
        ${messages.map((message) => html`<li>${message}</li>`)}
      </ul>
    </div>
  `;
}
