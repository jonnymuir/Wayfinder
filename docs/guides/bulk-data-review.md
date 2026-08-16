# Bulk data review

How Wayfinder handles bulk, row-level data — a modern, paginated "only show me what needs
attention" review experience for a file exchange with an external system that only ever speaks
whole-file-in/whole-file-out CSV, and can't be asked to speak anything else. This is for whoever
is *authoring* a service blueprint against a host that has registered an
`Abstractions.IBulkDatasetStore` implementation; if you're *building* a Wayfinder host, see
[Support systems](./support-systems.md) first — this feature is built directly on top of it.

This document is also exposed as an MCP resource (`service-blueprint-docs://bulk-data-review`)
so an agent can fetch it directly without repo access.

---

## The scenario this solves

The worked example (`Wayfinder.ReferenceApp/service-blueprints/njf-contributions.json`): the
National Juggling Federation (NJF) arranges a group public-liability policy with SafetyNet
Underwriting (the same fictional insurer from [Support systems](./support-systems.md)) and must
periodically submit a CSV contributions file listing its members, tiers, and premiums. SafetyNet's
contract can't change: upload a CSV, get back the same CSV with extra columns — a matched member
ID, and per-row error/warning status and text. Historically this meant fixing problems in Excel
and re-uploading the whole file. Wayfinder hides that mechanically for its own end user behind a
paginated, "only show me the rows that need attention" card UI, while SafetyNet still only ever
sees the exact whole-file shape it always expected.

## The abstraction: one action authors the whole dataset shape

A bulk dataset doesn't get its own registry the way a support system does — its shape is authored
directly on a single action, `bulk-dataset-ingest`, and everything downstream (the review
component, corrections, resubmission) reads that shape back out of the dataset store rather than
needing its own configuration. Two action types, both reusing `ActionDefinition.Parameters` the
same way `support-system-call` already does:

- **`bulk-dataset-ingest`** (an `onEnter` action) parses a file field's content into an indexed,
  pageable dataset. Its `columns` parameter — one entry per CSV column, each declaring a `role`
  (see below) — is the *only* place a bulk dataset's shape is authored.
- **`bulk-dataset-materialize`** (a route action) reconstructs the full CSV (original rows with
  any corrections overlaid) and writes it back into a file field, ready to resubmit through the
  same `support-system-call` action that produced the file in the first place — a genuine loop,
  which Wayfinder's routing already supports natively (a route may target any previously-visited
  stage; see `ProcessManagerEngine.MoveCursor`).

## `bulk-dataset-ingest` reference

| Param | Meaning |
|---|---|
| `sourceFileField` | Field-ref to the file to ingest — typically a `support-system-call` action's own declared file output (the external system's response), sometimes a directly-captured `file-upload` field (a wholesale replacement upload). |
| `columns` | Array of column descriptors — see below. Must declare at least one, and exactly one with `role: "RowKey"`. |
| `errorCountField` / `warningCountField` / `acceptedCountField` | Optional field-refs the ingest result's summary counts are written into, so ordinary calculation rules and route triggers can react to them without touching the dataset store directly — e.g. a route trigger `contributionsErrorCount = 0` to gate moving past the review stage. |

Each entry in `columns`:

| Field | Meaning |
|---|---|
| `key` | The literal CSV header this column binds to. |
| `title` | Human-readable label for the review UI. |
| `valueKind` | Same closed vocabulary as a component property's `ValueKind` (`String`, `Number`, `Integer`, `Boolean`, ...). |
| `format` | Optional semantic hint, e.g. `"currency"`, `"date"`. |
| `role` | One of `RowKey`, `Data`, `ResponseMatchedId`, `ResponseError`, `ResponseWarning`, `Ignored` — see below. |
| `visible` | Whether this column renders in the review UI at all. Defaults `true`. |
| `editable` | Whether a user may correct this column's value. Only meaningful when `role` is `Data`. |

**Column roles:**

- **`RowKey`** — the column the external system is expected to echo back unchanged. Exactly one
  per dataset; it's how a row is correlated across resubmission rounds (the real-world reason a
  bordereau carries a client reference column).
- **`Data`** — an ordinary business value, optionally `editable`.
- **`ResponseMatchedId`** — an identifier the external system assigned or matched on ingest.
  Always read-only.
- **`ResponseError`** / **`ResponseWarning`** — a non-empty value marks the row as needing
  attention; this is what drives the review UI's default filter. Always read-only.
- **`Ignored`** — present in the file but not shown or acted on.

## `bulk-dataset-materialize` reference

| Param | Meaning |
|---|---|
| `sourceFileField` | Must match some `bulk-dataset-ingest` action's own `sourceFileField` in this blueprint — it materializes a dataset ingest produced, it doesn't declare a new one. |
| `targetFileField` | Field-ref the materialized file is written into — typically the same field the original upload went to, so a route back to the automation queue resubmits it. |

## Validation

Registering these actions correctly gets a blueprint real, comprehensive validation for free —
`ServiceBlueprint.ValidateBulkDatasetActions()` (wired into the same `validate_service_blueprint`/
`save_service_blueprint` pipeline as every other structural check):

- `sourceFileField` is set and resolves to a known field — a captured input, or a support-system
  capability's own declared output (`BULK_DATASET_ACTION_MISSING_SOURCE_FIELD`/
  `_INVALID_SOURCE_FIELD`).
- A `bulk-dataset-ingest` action declares at least one column (`_MISSING_COLUMNS`), every column
  has both a `key` and `title` (`_INVALID_COLUMN`), no two columns share a `key`
  (`_DUPLICATE_COLUMN_KEY`), and every `role`/`valueKind` is one of the closed, known vocabularies
  (`_UNKNOWN_ROLE`/`_UNKNOWN_VALUE_KIND`).
- Exactly one column declares `role: "RowKey"` — never zero, never more than one
  (`_MISSING_ROW_KEY`/`_DUPLICATE_ROW_KEY_ROLE`).
- A `bulk-dataset-materialize` action's `sourceFileField` matches some `bulk-dataset-ingest`
  action's own `sourceFileField` elsewhere in the blueprint (`_UNKNOWN_DATASET`), and it declares
  a `targetFileField` (`_MISSING_TARGET_FIELD`).

Separately, `ValidateDataDisplayBindings()` treats any field key declared in an ingest action's
`errorCountField`/`warningCountField`/`acceptedCountField` as a known, legitimate binding for a
`summary-list`/`stat-group` anywhere in the blueprint — the same as a captured input field, a
`calculations.fields` entry, or a support system's own declared `Outputs`.

## Using it in a blueprint

```json
{
  "type": "bulk-dataset-ingest",
  "timing": "onEnter",
  "params": {
    "sourceFileField": "contributionsResponseFile",
    "errorCountField": "contributionsErrorCount",
    "warningCountField": "contributionsWarningCount",
    "acceptedCountField": "contributionsAcceptedCount",
    "columns": [
      { "key": "memberRef", "title": "Member reference", "valueKind": "String", "role": "RowKey" },
      { "key": "memberName", "title": "Name", "valueKind": "String", "role": "Data", "editable": true },
      { "key": "tier", "title": "Membership tier", "valueKind": "String", "role": "Data", "editable": true },
      { "key": "monthlyContribution", "title": "Monthly contribution", "valueKind": "Number", "format": "currency", "role": "Data", "editable": true },
      { "key": "safetyNetMemberId", "title": "SafetyNet member ID", "valueKind": "String", "role": "ResponseMatchedId" },
      { "key": "errorText", "title": "Errors", "valueKind": "String", "role": "ResponseError" },
      { "key": "warningText", "title": "Warnings", "valueKind": "String", "role": "ResponseWarning" }
    ]
  }
}
```

Placing a `BulkDataReviewComponent` on the same stage, bound to the same `sourceFileField`,
renders the review UI against whatever the ingest action's `columns` already declared — the
component itself needs no column configuration of its own (see
[Extending the component catalog](./extending-the-component-catalog.md) for the general pattern
this follows).

## Performance and security

Both are first-class requirements of the underlying `IBulkDatasetStore`, not afterthoughts —
see the type's own doc comments in `Wayfinder.Engine/Abstractions` for specifics:

- Ingest streams the source file row-by-row and never re-parses it — every subsequent read,
  correction, or export hits the already-indexed store. The full dataset is never sent to the
  browser; the review UI only ever requests one page of attention-rows at a time.
- Every dataset access is server-mediated and re-checks the caller's session against the owning
  service-request instance — the same discipline `IServiceRequestFileStorage` already applies, no
  signed URLs. A `DatasetId` is minted, never derived from guessable inputs.
- Each row keeps its originally-ingested values immutably; a correction is stored as an
  attributable overlay (who, when, old → new), never an in-place overwrite — the audit trail is
  data, not cryptography, since the flow is fully server-mediated end to end.
- A human-facing CSV download is sanitized against formula injection (a cell starting with `=`,
  `+`, `-`, `@`, tab, or CR is neutralized); the file resubmitted to the external system never is,
  since that would corrupt real data a machine parser depends on.
