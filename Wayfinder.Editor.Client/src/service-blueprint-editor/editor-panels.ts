import { type TemplateResult, nothing } from 'lit';
import { html, unsafeStatic } from 'lit/static-html.js';
import { renderToolbarIcon } from './graph/toolbar-icons.js';

export interface PanelHeader {
  title: string;
  /** Shown only while the panel is open. */
  subtitle: TemplateResult | string | null;
  collapsed: boolean;
  /** Test hook, e.g. `wayfinder-outline-toggle`. */
  toggleHook: string;
  controls: string;
  expandLabel: string;
  collapseLabel: string;
  /** Which way the chevron points to open the panel: the outline opens rightwards, the properties drawer leftwards. */
  opensTowards: 'right' | 'left';
  onToggle(): void;
}

/** A side panel's title row with its collapse/expand toggle. */
export function renderPanelHeader(panel: PanelHeader) {
  const [openIcon, closeIcon] =
    panel.opensTowards === 'right' ? (['chevronRight', 'chevronLeft'] as const) : (['chevronLeft', 'chevronRight'] as const);
  const label = panel.collapsed ? panel.expandLabel : panel.collapseLabel;
  return html`
    <div class="panel-header">
      <div class="panel-header-copy">
        <h2 class="panel-title">${panel.title}</h2>
        ${panel.collapsed || panel.subtitle === null ? nothing : html`<p class="panel-subtitle">${panel.subtitle}</p>`}
      </div>
      <button
        type="button"
        class="panel-toggle"
        ${unsafeStatic(`data-${panel.toggleHook}`)}
        aria-controls=${panel.controls}
        aria-expanded=${String(!panel.collapsed)}
        aria-label=${label}
        @click=${panel.onToggle}
      >
        ${renderToolbarIcon(panel.collapsed ? openIcon : closeIcon)}
        <span class="sr-only">${label}</span>
      </button>
    </div>
  `;
}

const plural = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`;

function healthSummary(errors: number, warnings: number): string {
  if (errors > 0 && warnings > 0) {
    return `${plural(errors, 'error')} and ${plural(warnings, 'warning')} need attention.`;
  }
  return errors > 0 ? `${plural(errors, 'validation error')} need attention.` : `${plural(warnings, 'validation warning')} need attention.`;
}

/** A strip above the canvas that says validation has something to report, with a way into the Validation tab. */
export function renderCanvasHealthHint(errors: number, warnings: number, onOpenValidation: () => void) {
  if (errors + warnings === 0) {
    return nothing;
  }
  return html`
    <div class=${`canvas-health-hint ${errors > 0 ? 'is-error' : 'is-warning'}`} data-wayfinder-canvas-health-hint role="status">
      <span class="canvas-health-summary">${healthSummary(errors, warnings)}</span>
      <button type="button" class="canvas-health-action" data-wayfinder-open-validation @click=${onOpenValidation}>Open Validation</button>
    </div>
  `;
}
