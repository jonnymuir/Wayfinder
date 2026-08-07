// Progressive enhancement for the wayfinder-slider markup Wayfinder.Rendering.GovUk renders by
// default: updates the live value readout as the range input is dragged. Deliberately doesn't
// re-run any calculation — this reference app is hand-rolled, server-rendered HTML with no
// client-side calc engine (see PageShell.cs's own remarks on that); "Recalculate" is a real POST,
// same as every other action here. Without this script the slider still works and still submits
// correctly — the readout just stays at its last-submitted value until the next recalculate.
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
