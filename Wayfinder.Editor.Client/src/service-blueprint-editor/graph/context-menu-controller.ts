import type { ReactiveController } from 'lit';
import { html, nothing } from 'lit';

export type ContextMenuTarget =
  | { kind: 'canvas' }
  | { kind: 'stage'; stageKey: string }
  | { kind: 'gateway'; gatewayKey: string }
  | { kind: 'transition'; transitionIndex: number };

/** The things the menu can be opened on that are not the empty canvas. */
export type NodeTarget = Exclude<ContextMenuTarget, { kind: 'canvas' }>;

type MenuState = ContextMenuTarget & { x: number; y: number };

/** What choosing a menu item asks the graph to do. */
export interface ContextMenuCommands {
  /** Opens "add stage", placed after `afterStageKey` or at the end. */
  addStage(afterStageKey: string | null): void;
  fitToScreen(): void;
  openInspector(target: NodeTarget): void;
  copyJson(target: NodeTarget): void | Promise<void>;
  remove(target: NodeTarget): void;
}

export interface ContextMenuContext {
  /** The element's box, which the menu is positioned within and kept inside. */
  bounds(): DOMRect;
  root(): ShadowRoot | null;
  commands(): ContextMenuCommands;
}

const MARGIN = 12;

const itemClass = (danger: boolean) => (danger ? 'danger' : '');

/**
 * The graph's right-click menu: where it opens (kept inside the element), what it offers for a stage,
 * gateway, route or the empty canvas, keyboard dismissal, and returning focus to what opened it.
 * Choosing an item runs the matching graph command and closes the menu.
 */
export class ContextMenuController implements ReactiveController {
  private _menu: MenuState | null = null;
  private _returnTarget: HTMLElement | null = null;

  constructor(
    private readonly _host: { addController(controller: ReactiveController): void; requestUpdate(): void },
    private readonly _context: ContextMenuContext
  ) {
    _host.addController(this);
  }

  hostConnected() {}

  /** Where focus returns when a dialog opened from the menu closes. */
  get returnTarget(): HTMLElement | null {
    return this._returnTarget;
  }

  open(position: { clientX: number; clientY: number }, target: ContextMenuTarget, returnTarget?: HTMLElement) {
    const bounds = this._context.bounds();
    this._menu = {
      ...target,
      x: Math.max(MARGIN, position.clientX - bounds.left),
      y: Math.max(MARGIN, position.clientY - bounds.top),
    };
    this._returnTarget = returnTarget ?? null;
    this._host.requestUpdate();
    requestAnimationFrame(() => this._focusAndKeepInside(bounds));
  }

  dismiss(restoreFocus = true) {
    this._menu = null;
    const returnTarget = this._returnTarget;
    this._returnTarget = null;
    if (restoreFocus) {
      requestAnimationFrame(() => returnTarget?.focus());
    }
    this._host.requestUpdate();
  }

  private _focusAndKeepInside(bounds: DOMRect) {
    const menu = this._context.root()?.querySelector<HTMLElement>('[data-wayfinder-context-menu]');
    menu?.querySelector<HTMLButtonElement>('button')?.focus();
    if (!menu || !this._menu) {
      return;
    }

    const rect = menu.getBoundingClientRect();
    const overflowX = rect.right - (bounds.left + bounds.width) + MARGIN;
    const overflowY = rect.bottom - (bounds.top + bounds.height) + MARGIN;
    if (overflowX > 0 || overflowY > 0) {
      this._menu = {
        ...this._menu,
        x: overflowX > 0 ? Math.max(MARGIN, this._menu.x - overflowX) : this._menu.x,
        y: overflowY > 0 ? Math.max(MARGIN, this._menu.y - overflowY) : this._menu.y,
      };
      this._host.requestUpdate();
    }
  }

  private _choose(run: (commands: ContextMenuCommands) => void | Promise<void>) {
    void run(this._context.commands());
    this.dismiss(false);
  }

  private _item(label: string, run: (commands: ContextMenuCommands) => void | Promise<void>, danger = false) {
    return html`<button type="button" role="menuitem" class=${itemClass(danger)} @click=${() => this._choose(run)}>${label}</button>`;
  }

  private _items(menu: MenuState) {
    if (menu.kind === 'canvas') {
      return html`${this._item('Add stage', (c) => c.addStage(null))}${this._item('Fit to screen', (c) => c.fitToScreen())}`;
    }

    const target: NodeTarget = menu;
    const addStage =
      menu.kind === 'stage'
        ? this._item('Add stage', (c) => c.addStage(menu.stageKey))
        : menu.kind === 'gateway'
          ? this._item('Add stage', (c) => c.addStage(null))
          : nothing;
    return html`
      ${addStage}
      ${this._item(`Open ${menu.kind} inspector`, (c) => c.openInspector(target))}
      ${this._item(`Copy ${menu.kind} JSON`, (c) => c.copyJson(target))}
      ${this._item(`Delete ${menu.kind}`, (c) => c.remove(target), true)}
    `;
  }

  render() {
    const menu = this._menu;
    if (!menu) {
      return nothing;
    }

    return html`
      <div
        class="context-menu"
        style=${`left:${menu.x}px;top:${menu.y}px;`}
        role="menu"
        aria-label="Graph workspace actions"
        data-wayfinder-context-menu
        @keydown=${(event: KeyboardEvent) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            this.dismiss();
          }
        }}
        @click=${(event: Event) => event.stopPropagation()}
      >
        ${this._items(menu)}
      </div>
    `;
  }
}
