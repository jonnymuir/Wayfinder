// Fires the current stage's own "recalculate" action automatically whenever a radio/range/number
// input settles, instead of requiring an explicit button click — the genuinely-live feel an
// interactive-island stage (slider/stat-group/chart-driven modelling) needs. Deliberately not a
// client-side reimplementation of the calculation itself: the server (this blueprint's own
// calculations block, evaluated by Wayfinder.Engine) stays the single place the formulas live —
// this script only avoids a full page navigation to get their result back, via the exact same
// POST a manual click on the "Recalculate" button already performs, then swaps the result into
// the page in place. A stage with no "recalculate" action (most stages) is untouched entirely.
document.addEventListener('change', async (event) => {
  const target = event.target;
  if (!(target instanceof HTMLInputElement) || !target.matches('input[type="radio"], input[type="range"], input[type="number"]')) {
    return;
  }

  const form = target.closest('form');
  if (!form) {
    return;
  }

  const recalculateButton = form.querySelector('button[name="action"][value="recalculate"]');
  if (!recalculateButton) {
    return; // This stage doesn't offer live recalculation — leave the normal submit flow alone.
  }

  const formData = new FormData(form);
  formData.set('action', recalculateButton.getAttribute('value'));

  // Not form.action: this form has a submit button literally named "action" (the field the
  // server reads to know which route was taken), and a named form control with the same name
  // as one of <form>'s own IDL properties shadows it — form.action returns a RadioNodeList of
  // the buttons here, not the URL string. The action attribute itself is unaffected.
  const postUrl = form.getAttribute('action');

  let response;
  try {
    response = await fetch(postUrl, { method: 'POST', body: formData });
  } catch {
    return; // Offline/network hiccup — the visible "Recalculate" button still works as a fallback.
  }
  if (!response.ok) {
    return;
  }

  const html = await response.text();
  const freshJourney = new DOMParser().parseFromString(html, 'text/html').getElementById('wayfinder-journey');
  const currentJourney = document.getElementById('wayfinder-journey');
  if (!freshJourney || !currentJourney) {
    return;
  }

  // Swapping in a whole fresh subtree tears down whatever had focus — for a keyboard user
  // adjusting a slider with the arrow keys (each press is its own "change"), losing focus after
  // every single keypress would make the control unusable. Re-find and refocus the same field
  // (matched by id — every field here renders one) in the freshly swapped-in markup.
  const focusedId = target.id || null;
  currentJourney.replaceWith(freshJourney);
  if (focusedId) {
    document.getElementById(focusedId)?.focus();
  }
});
