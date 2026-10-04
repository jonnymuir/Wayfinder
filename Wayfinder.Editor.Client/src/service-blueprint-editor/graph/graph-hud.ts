import { type TemplateResult, nothing } from 'lit';
import { html, unsafeStatic } from 'lit/static-html.js';

export interface HudActions {
  addStage(trigger: HTMLElement): void;
  addGateway(trigger: HTMLElement): void;
  tidyLayout(): void;
  zoomOut(): void;
  zoomIn(): void;
  fitToScreen(): void;
  fitToWidth(): void;
}

interface HudButton {
  glyph: string;
  label: string;
  onClick(event: Event): void;
  /** Test hook, e.g. `wayfinder-add-stage` renders as the `data-wayfinder-add-stage` attribute. */
  hook?: string;
}

const button = ({ glyph, label, onClick, hook }: HudButton) =>
  html`<button type="button" class="hud-button hud-button--icon" ${unsafeStatic(hook ? `data-${hook}` : '')} aria-label=${label} title=${label} @click=${onClick}>
    <span aria-hidden="true">${glyph}</span>
  </button>`;

/** The graph's own workspace controls, shown when its host does not supply a consolidated toolbar. */
export function renderGraphHud(actions: HudActions, zoomPercent: number, readOnly: boolean): TemplateResult {
  return html`
    <div class="graph-hud" aria-label="Workspace controls and hints">
      ${
        readOnly
          ? nothing
          : html`
            <div class="hud-group">
              ${button({ glyph: '▭+', label: 'Add stage', hook: 'wayfinder-add-stage', onClick: (event) => actions.addStage(event.currentTarget as HTMLElement) })}
              ${button({ glyph: '◇+', label: 'Add gateway', hook: 'wayfinder-add-gateway', onClick: (event) => actions.addGateway(event.currentTarget as HTMLElement) })}
              ${button({ glyph: '▦', label: 'Tidy layout', hook: 'wayfinder-auto-arrange', onClick: () => actions.tidyLayout() })}
            </div>
          `
      }
      <div class="hud-group">
        ${button({ glyph: '−', label: 'Zoom out', onClick: () => actions.zoomOut() })}
        <span class="zoom-indicator" data-wayfinder-zoom>${zoomPercent}%</span>
        ${button({ glyph: '+', label: 'Zoom in', onClick: () => actions.zoomIn() })}
        ${button({ glyph: '⛶', label: 'Fit to screen', hook: 'wayfinder-fit-screen', onClick: () => actions.fitToScreen() })}
        ${button({ glyph: '↔', label: 'Fit width', hook: 'wayfinder-fit-width', onClick: () => actions.fitToWidth() })}
      </div>
    </div>
  `;
}

/** What the canvas shows instead of a diagram when the blueprint has no stages or gateways. */
export function renderWorkspaceEmptyState(readOnly: boolean, onAddFirstStage: (trigger: HTMLElement) => void): TemplateResult {
  return html`
    <section class="workspace-empty-state" role="status" data-wayfinder-empty-state="graph">
      <h2 class="workspace-empty-title">${readOnly ? 'No stages to display' : 'Start building your service blueprint'}</h2>
      <p class="workspace-empty-copy">
        ${
          readOnly
            ? 'This serviceBlueprint has no stages.'
            : 'This serviceBlueprint does not have any stages yet. Add the first stage, then connect routes as you model the author journey.'
        }
      </p>
      ${
        readOnly
          ? nothing
          : html`
            <ul class="workspace-empty-tips">
              <li>Use <strong>Add stage</strong>, then choose the queue that should own the work.</li>
              <li><strong>Add the next stage before you branch</strong> — gateways always connect existing stages, never empty space.</li>
              <li>Use the editor Help button or press <strong>F1</strong> to review shortcuts while you work.</li>
            </ul>
            <div class="workspace-empty-actions">
              <button type="button" class="hud-button" data-wayfinder-empty-add-stage @click=${(event: Event) => onAddFirstStage(event.currentTarget as HTMLElement)}>
                Add first stage
              </button>
            </div>
          `
      }
    </section>
  `;
}
