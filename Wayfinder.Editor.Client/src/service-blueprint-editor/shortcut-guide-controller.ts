import { type ReactiveController, type TemplateResult, html, nothing } from 'lit';
import type { ReactiveControllerHost } from 'lit';
import { SERVICE_BLUEPRINT_SHORTCUT_GROUPS } from './editor-shortcuts.js';

type HostWithRoot = ReactiveControllerHost & { renderRoot: HTMLElement | DocumentFragment };

/** The help dialog listing keyboard shortcuts: opening it, keeping focus inside while open, and returning focus on close. */
export class ShortcutGuideController implements ReactiveController {
  isOpen = false;
  private returnTarget: HTMLElement | null = null;

  constructor(private readonly host: HostWithRoot) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.returnTarget = null;
  }

  open(activator?: HTMLElement | null): void {
    this.returnTarget = activator ?? null;
    this.isOpen = true;
    this.host.requestUpdate();
    requestAnimationFrame(() => {
      this.host.renderRoot.querySelector<HTMLElement>('[data-wayfinder-help-close]')?.focus();
    });
  }

  close(): void {
    this.isOpen = false;
    this.returnTarget?.focus();
    this.returnTarget = null;
    this.host.requestUpdate();
  }

  /** Escape closes; Tab and Shift+Tab wrap inside the dialog. */
  private trapFocus(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
      return;
    }

    if (event.key !== 'Tab') {
      return;
    }

    const root = event.currentTarget as HTMLElement;
    const focusable = Array.from(
      root.querySelectorAll<HTMLElement>('button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])')
    ).filter((element) => !element.hasAttribute('disabled') && element.tabIndex >= 0);
    if (focusable.length === 0) {
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = (this.host.renderRoot as ShadowRoot).activeElement as HTMLElement | null;
    if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  render(): TemplateResult | typeof nothing {
    if (!this.isOpen) {
      return nothing;
    }

    return html`
      <div
        class="modal-backdrop"
        role="presentation"
        @click=${(event: MouseEvent) => {
          if (event.target === event.currentTarget) {
            this.close();
          }
        }}
      >
        <section
          class="shortcut-dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby="service-blueprint-shortcut-title"
          aria-describedby="service-blueprint-shortcut-copy"
          data-wayfinder-shortcut-dialog
          @keydown=${(event: KeyboardEvent) => this.trapFocus(event)}
        >
          <div class="shortcut-dialog-header">
            <div>
              <p class="shortcut-dialog-eyebrow">Help and shortcuts</p>
              <h2 id="service-blueprint-shortcut-title" class="shortcut-dialog-title">Service Blueprint editor keyboard reference</h2>
              <p id="service-blueprint-shortcut-copy" class="shortcut-dialog-copy">
                These shortcuts stay visible in the editor so authors do not have to memorise them. Open this guide any time with F1.
              </p>
            </div>
            <button
              type="button"
              class="toolbar-btn shortcut-dialog-close"
              data-wayfinder-help-close
              @click=${() => this.close()}
            >
              Close
            </button>
          </div>

          <div class="shortcut-groups">
            ${SERVICE_BLUEPRINT_SHORTCUT_GROUPS.map(
              (group) => html`
              <section class="shortcut-group" data-wayfinder-shortcut-group=${group.id}>
                <h3 class="shortcut-group-title">${group.title}</h3>
                <ol class="shortcut-list">
                  ${group.shortcuts.map(
                    (shortcut) => html`
                    <li class="shortcut-item" data-wayfinder-shortcut=${shortcut.id}>
                      <div class="shortcut-copy">
                        <p class="shortcut-command">${shortcut.command}</p>
                        <p class="shortcut-description">${shortcut.description}</p>
                      </div>
                      <div class="shortcut-keys" aria-label=${`${shortcut.command} shortcuts`}>
                        ${shortcut.labels.map((label) => html`<kbd>${label}</kbd>`)}
                      </div>
                      <p class="shortcut-context">${shortcut.context}</p>
                    </li>
                  `
                  )}
                </ol>
              </section>
            `
            )}
          </div>

          <section class="shortcut-group" data-wayfinder-shortcut-group="quick-tips">
            <h3 class="shortcut-group-title">Quick tips</h3>
            <ul class="help-tip-list">
              <li>Each queue is one <strong>vertical service column</strong>. Read the service blueprint <strong>top to bottom</strong>.</li>
              <li>Stages are the work cards. Gateways are the diamond routing points between them.</li>
              <li>Use the <strong>Outline</strong> panel on the left to jump between queue columns and stages quickly.</li>
              <li>Reorder stages in <strong>List view</strong> with <strong>Move up</strong>, <strong>Move down</strong>, or <strong>Alt + Arrow</strong>. The canvas keeps its automatic layout in this first pass.</li>
              <li>Use the <strong>Validation</strong> tab to check for issues before you save.</li>
              <li>All structural changes support <strong>Undo/Redo</strong> — experiment safely.</li>
            </ul>
          </section>

          <section class="shortcut-group" data-wayfinder-shortcut-group="getting-started">
            <h3 class="shortcut-group-title">Getting started</h3>
            <ol class="help-tip-list">
              <li>Start on the <strong>Canvas</strong> tab and add the first stage for the queue that owns the work.</li>
              <li>Add the next stage that should happen in the service flow, then open the <strong>Inspector</strong> to shape its details.</li>
              <li>Add a <strong>routing gateway</strong> when the service blueprint needs to branch or wait for multiple paths to join.</li>
              <li>Create routes so the canvas reads as <strong>stage → gateway → stage</strong> or <strong>gateway → gateway</strong>.</li>
              <li>Check <strong>Validation</strong> before saving.</li>
              <li>Save your service blueprint when ready — changes will be published to the runtime.</li>
            </ol>
          </section>
        </section>
      </div>
    `;
  }
}
