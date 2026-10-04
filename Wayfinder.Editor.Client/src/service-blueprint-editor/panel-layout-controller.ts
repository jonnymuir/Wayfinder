import type { ReactiveController, ReactiveControllerHost } from 'lit';

const MIN_INSPECTOR_WIDTH = 280;
const MAX_INSPECTOR_WIDTH = 720;
const KEYBOARD_RESIZE_STEP = 16;

/**
 * How much room the outline (left) and the Properties inspector (right) take. Both start collapsed: the canvas is the
 * primary surface and either panel is one click away. The inspector opens itself when something is selected (a closed
 * Properties panel right after selecting a stage would just look broken) but is never re-collapsed except by the author.
 */
export class PanelLayoutController implements ReactiveController {
  outlineCollapsed = true;
  inspectorCollapsed = true;
  /** Expanded width of the Properties panel in px, dragged via its handle. */
  inspectorWidth = 380;
  resizing = false;

  private dragStartX = 0;
  private dragStartWidth = 0;

  constructor(private readonly host: ReactiveControllerHost) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.stopDragListeners();
  }

  /** The CSS custom properties the editor shell lays itself out with. */
  get shellStyle(): string {
    const outline = this.outlineCollapsed ? '3.5rem' : '240px';
    const inspector = this.inspectorCollapsed ? '3.5rem' : `${this.inspectorWidth}px`;
    return `--outline-width:${outline};--inspector-width:${inspector};`;
  }

  toggleOutline = () => {
    this.outlineCollapsed = !this.outlineCollapsed;
    this.host.requestUpdate();
  };

  toggleInspector = () => {
    this.inspectorCollapsed = !this.inspectorCollapsed;
    this.host.requestUpdate();
  };

  expandInspector(): void {
    if (this.inspectorCollapsed) {
      this.inspectorCollapsed = false;
      this.host.requestUpdate();
    }
  }

  // The inspector sits on the right, so dragging its handle left (a shrinking clientX) should widen it.
  startResize = (event: PointerEvent) => {
    event.preventDefault();
    this.dragStartX = event.clientX;
    this.dragStartWidth = this.inspectorWidth;
    this.resizing = true;
    window.addEventListener('pointermove', this.onPointerMove);
    window.addEventListener('pointerup', this.onPointerUp);
    this.host.requestUpdate();
  };

  resizeWithKeyboard = (event: KeyboardEvent) => {
    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      this.setWidth(this.inspectorWidth + KEYBOARD_RESIZE_STEP);
    } else if (event.key === 'ArrowRight') {
      event.preventDefault();
      this.setWidth(this.inspectorWidth - KEYBOARD_RESIZE_STEP);
    }
  };

  private onPointerMove = (event: PointerEvent) => {
    this.setWidth(this.dragStartWidth + (this.dragStartX - event.clientX));
  };

  private onPointerUp = () => {
    this.resizing = false;
    this.stopDragListeners();
    this.host.requestUpdate();
  };

  private stopDragListeners(): void {
    window.removeEventListener('pointermove', this.onPointerMove);
    window.removeEventListener('pointerup', this.onPointerUp);
  }

  private setWidth(width: number): void {
    this.inspectorWidth = Math.min(MAX_INSPECTOR_WIDTH, Math.max(MIN_INSPECTOR_WIDTH, width));
    this.host.requestUpdate();
  }
}
