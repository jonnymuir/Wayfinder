import { type ReactiveController, type ReactiveControllerHost, type TemplateResult, html, nothing } from 'lit';

const DISMISS_AFTER_MS = 5000;

/** A short confirmation message that dismisses itself. */
export class ToastController implements ReactiveController {
  message: string | null = null;
  private timer: number | null = null;

  constructor(private readonly host: ReactiveControllerHost) {
    host.addController(this);
  }

  hostDisconnected(): void {
    this.cancel();
  }

  show(message: string): void {
    this.cancel();
    this.message = message;
    this.timer = window.setTimeout(() => {
      this.message = null;
      this.timer = null;
      this.host.requestUpdate();
    }, DISMISS_AFTER_MS);
    this.host.requestUpdate();
  }

  private cancel(): void {
    if (this.timer !== null && typeof window !== 'undefined') {
      window.clearTimeout(this.timer);
    }
    this.timer = null;
  }

  render(): TemplateResult | typeof nothing {
    if (!this.message) {
      return nothing;
    }
    return html`
      <div class="toast-banner" role="status" aria-live="assertive" data-wayfinder-toast>
        ${this.message}
      </div>
    `;
  }
}
