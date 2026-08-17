// Progressively enhances the bulk-data-review markup GovUkComponents.RenderBulkDataReview emits
// (see docs/guides/bulk-data-review.md): fetches a dataset's summary + a page of rows from its
// own REST endpoints (data-wayfinder-bulk-review-api, a host-routed base URL a host's own
// rendering post-processing fills in — see ComponentRenderPayload.BulkDatasetApiUrl, the same
// "host supplies the URL, this package supplies the behaviour" shape WithFileDownloadUrls already
// uses for file-upload fields) and renders GOV.UK-styled cards, one row needing attention at a
// time, with inline correction of editable fields.
//
// The first fetch()-based component in this package — every other one here is server-rendered
// HTML with plain form posts (see wayfinder-poll.js's own remarks on that convention) — because
// paging/filtering/correcting individual rows against a dataset that can run to thousands of
// rows is genuinely interactive state a full page reload per click would make painful at any
// real scale (see docs/guides/bulk-data-review.md's performance principles: the full dataset is
// never sent to the browser, only one page of rows at a time, which is exactly what this script
// asks for). The <noscript> fallback already in the server-rendered markup (a plain download
// link, always present) still works with JavaScript off.
// Shipped as this package's own static web asset
// (/_content/Wayfinder.Rendering.GovUk/js/wayfinder-bulk-data-review.js).

function escapeHtml(value) {
  const div = document.createElement('div');
  div.textContent = value === null || value === undefined ? '' : String(value);
  return div.innerHTML;
}

function initBulkReview(root) {
  const apiBase = root.getAttribute('data-wayfinder-bulk-review-api');
  const pageSize = Number(root.getAttribute('data-wayfinder-bulk-review-page-size')) || 20;
  const summaryEl = root.querySelector('[data-wayfinder-bulk-review-summary]');
  const controlsEl = root.querySelector('[data-wayfinder-bulk-review-controls]');
  const rowsEl = root.querySelector('[data-wayfinder-bulk-review-rows]');
  const paginationEl = root.querySelector('[data-wayfinder-bulk-review-pagination]');
  const pageStatusEl = root.querySelector('[data-wayfinder-bulk-review-page-status]');
  const prevWrapper = root.querySelector('[data-wayfinder-bulk-review-prev-wrapper]');
  const nextWrapper = root.querySelector('[data-wayfinder-bulk-review-next-wrapper]');
  const prevButton = root.querySelector('[data-wayfinder-bulk-review-prev]');
  const nextButton = root.querySelector('[data-wayfinder-bulk-review-next]');
  const filterButtons = root.querySelectorAll('[data-wayfinder-bulk-review-filter]');
  const form = root.closest('form');

  const state = { filter: 'NeedsAttention', page: 0, columns: [] };

  // Corrections autosave (debounced) rather than needing an explicit "Save correction" click —
  // a manual save button meant a second edit made after clicking it, but before clicking it
  // again, silently never reached the server: "Resubmit corrected file" materializes whatever
  // the STORE last had, not whatever's currently sitting in the input box. rowKey -> flush()
  // for every row with a save either pending (debounced) or in flight; a clean save removes its
  // own entry. flushAll() is the safety net every navigation away from the current rows (page,
  // filter, or the stage's own route-trigger form submit — Resubmit/Accept and finish/etc, all
  // sharing one <form> per RenderForm) waits on first, so a still-focused, not-yet-debounced
  // edit can never be silently left behind either.
  const pendingSaves = new Map();
  const flushAll = () => Promise.all([...pendingSaves.values()].map((flush) => flush()));

  if (form) {
    form.addEventListener('submit', (event) => {
      if (pendingSaves.size === 0) {
        return;
      }

      event.preventDefault();
      const submitter = event.submitter;
      flushAll().then(() => {
        if (pendingSaves.size > 0) {
          // A save genuinely failed — its own row already explains why (see setSaveStatus's
          // 'error' state). Don't submit with an unsaved change silently left behind; the
          // caseworker can retry the edit once they've seen it.
          return;
        }

        if (typeof form.requestSubmit === 'function') {
          form.requestSubmit(submitter ?? undefined);
        } else {
          form.submit();
        }
      });
    });
  }

  function fetchJson(url, options) {
    return fetch(url, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json', ...(options?.headers ?? {}) },
      ...options,
    }).then((response) => (response.ok ? response.json() : null));
  }

  function loadSummary() {
    return fetchJson(`${apiBase}/summary`).then((summary) => {
      if (!summary) {
        summaryEl.innerHTML = '<p class="govuk-body">Could not load this file&rsquo;s summary.</p>';
        return;
      }

      state.columns = summary.columns ?? [];
      summaryEl.innerHTML = `<p class="govuk-body">${escapeHtml(summary.totalRowCount)} rows in total &mdash; ` +
        `${escapeHtml(summary.errorRowCount)} with errors, ${escapeHtml(summary.warningRowCount)} with warnings, ` +
        `${escapeHtml(summary.acceptedRowCount)} accepted.</p>`;
      controlsEl.hidden = false;
    });
  }

  function loadRows() {
    rowsEl.innerHTML = '<p class="govuk-body">Loading&hellip;</p>';
    const url = `${apiBase}/rows?filter=${encodeURIComponent(state.filter)}&page=${state.page}&pageSize=${pageSize}`;
    return fetchJson(url).then((page) => {
      if (!page) {
        rowsEl.innerHTML = '<p class="govuk-body">Could not load these rows.</p>';
        return;
      }

      renderRows(page);
    });
  }

  function renderRows(page) {
    rowsEl.innerHTML = '';
    if (page.rows.length === 0) {
      rowsEl.innerHTML = '<p class="govuk-body">No rows to show for this filter.</p>';
    } else {
      page.rows.forEach((row) => rowsEl.appendChild(renderRowCard(row)));
    }

    const totalPages = Math.max(1, Math.ceil(page.totalMatchingRowCount / pageSize));
    pageStatusEl.textContent = `Page ${page.pageIndex + 1} of ${totalPages}`;
    // The real GOV.UK pagination component omits a prev/next link entirely at either end of the
    // range rather than rendering it disabled — matching that (via hidden, since this is one
    // persistent page, not a server-rendered one per navigation) rather than a disabled affordance.
    prevWrapper.hidden = page.pageIndex <= 0;
    nextWrapper.hidden = page.pageIndex + 1 >= totalPages;
    paginationEl.hidden = page.totalMatchingRowCount === 0;
  }

  function renderField(row, column) {
    const value = row.currentValues[column.key];

    if (column.role === 'Data' && column.editable) {
      const inputId = `row-${escapeHtml(row.rowKey)}-${escapeHtml(column.key)}`;
      return `<div class="govuk-form-group wayfinder-bulk-review__field">
        <label class="govuk-label govuk-label--s" for="${inputId}">${escapeHtml(column.title)}</label>
        <input class="govuk-input" id="${inputId}" data-wayfinder-bulk-review-input="${escapeHtml(column.key)}" value="${escapeHtml(value)}">
      </div>`;
    }

    const valueClass = column.role === 'ResponseError' ? ' wayfinder-bulk-review__field-value--error'
      : column.role === 'ResponseWarning' ? ' wayfinder-bulk-review__field-value--warning' : '';
    return `<div class="wayfinder-bulk-review__field">
      <div class="wayfinder-bulk-review__field-label">${escapeHtml(column.title)}</div>
      <div class="wayfinder-bulk-review__field-value${valueClass}">${value ? escapeHtml(value) : '&mdash;'}</div>
    </div>`;
  }

  function renderRowCard(row) {
    const card = document.createElement('div');
    card.className = 'wayfinder-bulk-review__card' +
      (row.hasError ? ' wayfinder-bulk-review__card--error' : row.hasWarning ? ' wayfinder-bulk-review__card--warning' : '');

    const tag = row.hasError
      ? '<strong class="govuk-tag govuk-tag--red">Error</strong>'
      : row.hasWarning
        ? '<strong class="govuk-tag govuk-tag--yellow">Warning</strong>'
        : '';

    const fields = state.columns
      .filter((column) => column.visible !== false && column.role !== 'Ignored')
      .map((column) => renderField(row, column))
      .join('');

    const structuralIssue = row.structuralIssue
      ? `<p class="govuk-body wayfinder-bulk-review__structural-issue">${escapeHtml(row.structuralIssue)}</p>`
      : '';

    card.innerHTML = `
      <div class="wayfinder-bulk-review__card-header">
        <h3 class="govuk-heading-s wayfinder-bulk-review__card-title">${escapeHtml(row.rowKey)}</h3>${tag}
      </div>
      ${structuralIssue}
      <div class="wayfinder-bulk-review__fields">${fields}</div>
      <p class="wayfinder-bulk-review__save-status" data-wayfinder-bulk-review-save-status role="status"></p>
    `;

    const inputs = card.querySelectorAll('[data-wayfinder-bulk-review-input]');
    const saveStatus = card.querySelector('[data-wayfinder-bulk-review-save-status]');

    function setSaveStatus(text, tone) {
      saveStatus.textContent = text;
      saveStatus.className = 'wayfinder-bulk-review__save-status' +
        (tone ? ` wayfinder-bulk-review__save-status--${tone}` : '');
    }

    let dirty = false;
    let debounceTimer = null;
    // Serializes save attempts for this row so a debounce firing while a previous save is still
    // in flight can't send two overlapping POSTs — each waits for the last to settle, then sends
    // whatever's currently in the inputs (always read live, at send time, never a stale snapshot
    // captured back when the edit first happened).
    let saveChain = Promise.resolve();

    function saveNow() {
      dirty = false;
      setSaveStatus('Saving…', 'pending');
      const values = {};
      inputs.forEach((input) => {
        values[input.getAttribute('data-wayfinder-bulk-review-input')] = input.value;
      });

      saveChain = saveChain
        .then(() => fetch(`${apiBase}/rows/${encodeURIComponent(row.rowKey)}/correct`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify(values),
        }))
        .then((response) => {
          if (response.ok) {
            setSaveStatus('Saved', 'saved');
            if (!dirty) {
              pendingSaves.delete(row.rowKey);
            }
          } else {
            dirty = true;
            setSaveStatus('Could not save — try again.', 'error');
          }
        })
        .catch(() => {
          dirty = true;
          setSaveStatus('Could not save — try again.', 'error');
        });

      return saveChain;
    }

    function flush() {
      if (debounceTimer) {
        clearTimeout(debounceTimer);
        debounceTimer = null;
      }
      return dirty ? saveNow() : saveChain;
    }

    inputs.forEach((input) => {
      input.addEventListener('input', () => {
        dirty = true;
        setSaveStatus('Unsaved changes…', 'pending');
        pendingSaves.set(row.rowKey, flush);
        if (debounceTimer) {
          clearTimeout(debounceTimer);
        }
        debounceTimer = setTimeout(() => {
          debounceTimer = null;
          saveNow();
        }, 600);
      });
    });

    return card;
  }

  filterButtons.forEach((button) => {
    button.addEventListener('click', () => {
      flushAll().then(() => {
        state.filter = button.getAttribute('data-wayfinder-bulk-review-filter');
        state.page = 0;
        filterButtons.forEach((b) => b.setAttribute('aria-pressed', b === button ? 'true' : 'false'));
        loadRows();
      });
    });
  });

  prevButton.addEventListener('click', (event) => {
    event.preventDefault();
    if (state.page > 0) {
      flushAll().then(() => {
        state.page -= 1;
        loadRows();
      });
    }
  });

  nextButton.addEventListener('click', (event) => {
    event.preventDefault();
    flushAll().then(() => {
      state.page += 1;
      loadRows();
    });
  });

  loadSummary().then(loadRows);
}

function boot() {
  document.querySelectorAll('[data-wayfinder-bulk-review]').forEach(initBulkReview);
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
