// Initializes the real govuk-frontend package vendored alongside this script (see
// ../govuk-frontend/) — the exact "Importing JavaScript" quick-start from govuk-frontend's own
// README. A host loads this one script (type="module") instead of vendoring govuk-frontend
// itself and writing this three-line snippet again: the CSS/JS pair here is version-locked to
// whatever Wayfinder.Rendering.GovUk's own generated markup actually targets, so there's no
// separate "did I bump govuk-frontend and the renderer out of sync" risk for a host to manage.
import { initAll } from '../govuk-frontend/govuk-frontend.min.js';
initAll();
