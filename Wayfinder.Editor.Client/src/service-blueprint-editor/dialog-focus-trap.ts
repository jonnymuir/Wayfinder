/**
 * Keyboard handling shared by the editor's modal dialogs: Escape closes, and Tab wraps within the
 * dialog so focus never leaves the modal. Attach to the dialog panel's `keydown`;
 * `activeElement` is where focus currently is within the owning shadow root.
 */
const FOCUSABLE = 'button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])';

export function trapDialogFocus(event: KeyboardEvent, activeElement: () => Element | null | undefined, onClose: () => void): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    onClose();
    return;
  }
  if (event.key !== 'Tab') {
    return;
  }

  const focusable = Array.from((event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (element) => !element.hasAttribute('disabled') && element.tabIndex >= 0
  );
  if (focusable.length === 0) {
    return;
  }

  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = activeElement();
  if (event.shiftKey && active === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
}
