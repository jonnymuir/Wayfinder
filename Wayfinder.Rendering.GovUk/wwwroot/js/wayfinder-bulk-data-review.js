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
  const prevButton = root.querySelector('[data-wayfinder-bulk-review-prev]');
  const nextButton = root.querySelector('[data-wayfinder-bulk-review-next]');
  const filterButtons = root.querySelectorAll('[data-wayfinder-bulk-review-filter]');

  const state = { filter: 'NeedsAttention', page: 0, columns: [] };

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
    prevButton.disabled = page.pageIndex <= 0;
    nextButton.disabled = page.pageIndex + 1 >= totalPages;
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
        <span class="wayfinder-bulk-review__card-title">${escapeHtml(row.rowKey)}</span>${tag}
      </div>
      ${structuralIssue}
      <div class="wayfinder-bulk-review__fields">${fields}</div>
      <div class="govuk-button-group">
        <button type="button" class="govuk-button" data-wayfinder-bulk-review-save>Save correction</button>
        <span class="wayfinder-bulk-review__save-status" data-wayfinder-bulk-review-save-status role="status"></span>
      </div>
    `;

    const saveButton = card.querySelector('[data-wayfinder-bulk-review-save]');
    const saveStatus = card.querySelector('[data-wayfinder-bulk-review-save-status]');
    saveButton.addEventListener('click', () => {
      const values = {};
      card.querySelectorAll('[data-wayfinder-bulk-review-input]').forEach((input) => {
        values[input.getAttribute('data-wayfinder-bulk-review-input')] = input.value;
      });

      saveButton.disabled = true;
      saveStatus.textContent = 'Saving…';
      fetch(`${apiBase}/rows/${encodeURIComponent(row.rowKey)}/correct`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify(values),
      })
        .then((response) => {
          saveButton.disabled = false;
          saveStatus.textContent = response.ok ? 'Saved' : 'Could not save — try again.';
        })
        .catch(() => {
          saveButton.disabled = false;
          saveStatus.textContent = 'Could not save — try again.';
        });
    });

    return card;
  }

  filterButtons.forEach((button) => {
    button.addEventListener('click', () => {
      state.filter = button.getAttribute('data-wayfinder-bulk-review-filter');
      state.page = 0;
      filterButtons.forEach((b) => b.setAttribute('aria-pressed', b === button ? 'true' : 'false'));
      loadRows();
    });
  });

  prevButton.addEventListener('click', () => {
    if (state.page > 0) {
      state.page -= 1;
      loadRows();
    }
  });

  nextButton.addEventListener('click', () => {
    state.page += 1;
    loadRows();
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
