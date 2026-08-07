// Progressive enhancement for the wayfinder-slider markup Wayfinder.Rendering.GovUk renders by
// default: updates the live value readout as the range input is dragged. Deliberately doesn't
// re-run any calculation — there is no client-side calc engine anywhere in Wayfinder yet, only
// the server-side one (Wayfinder/Services/Calculations); "Recalculate" (see
// wayfinder-live-recalculate.js) is a real POST, same as every other action. Without this script
// the slider still works and still submits correctly — the readout just stays at its
// last-submitted value until the next recalculate. Shipped as this package's own static web
// asset (/_content/Wayfinder.Rendering.GovUk/js/wayfinder-slider.js) — any host that renders the
// slider markup loads this same file rather than hand-copying its own.
document.addEventListener('input', (event) => {
  const input = event.target;
  if (!(input instanceof HTMLInputElement) || !input.matches('[data-wayfinder-slider-input]')) {
    return;
  }

  const wrapper = input.closest('[data-wayfinder-slider]');
  const readout = wrapper ? wrapper.querySelector('[data-wayfinder-slider-value]') : null;
  if (readout) {
    readout.textContent = `${readout.dataset.prefix ?? ''}${input.value}${readout.dataset.suffix ?? ''}`;
  }
});
