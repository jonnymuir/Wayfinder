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
  pageable dataset, minting a fresh dataset id and writing it into `datasetIdField`. Its `columns`
  parameter — one entry per CSV column, each declaring a `role` (see below) — is the *only* place
  a bulk dataset's shape is authored.
- **`bulk-dataset-materialize`** (an `onEnter` action, typically on the same stage a loop's
  `support-system-call` re-runs on, ordered before it) reconstructs the full CSV (original rows
  with any corrections overlaid) for the dataset named by `datasetIdField`, and writes it back
  into a file field — ready for that stage's own `support-system-call` action to resubmit through
  the same call that produced the file in the first place. A genuine loop: a route may target any
  previously-visited stage/gateway, which Wayfinder's routing already supports natively (see
  `ProcessManagerEngine.MoveCursor`). Materialize is a safe no-op the first time round — before
  anything's been ingested, `datasetIdField` has no value yet, so it leaves `targetFileField`
  untouched and the original upload goes through as-is.

## `bulk-dataset-ingest` reference

| Param | Meaning |
|---|---|
| `sourceFileField` | Field-ref to the file to ingest — typically a `support-system-call` action's own declared file output (the external system's response), sometimes a directly-captured `file-upload` field (a wholesale replacement upload). |
| `datasetIdField` | Field-ref the freshly-minted dataset id is written into — the single identifier a later `bulk-dataset-materialize` action or a `BulkDataReviewComponent` binds to. Deliberately not `sourceFileField` itself: materialize runs on a different stage than ingest, sometimes several loop rounds later, and only ever has `ServiceRequest.FieldValues` to read from. |
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
| `datasetIdField` | Must match some `bulk-dataset-ingest` action's own `datasetIdField` in this blueprint — it materializes a dataset ingest produced, it doesn't declare a new one. |
| `targetFileField` | Field-ref the materialized file is written into — typically the same field the original upload went to, so a route back to the automation queue resubmits it. |

## Validation

Registering these actions correctly gets a blueprint real, comprehensive validation for free —
`ServiceBlueprint.ValidateBulkDatasetActions()` (wired into the same `validate_service_blueprint`/
`save_service_blueprint` pipeline as every other structural check):

- Both action types must set `datasetIdField` (`BULK_DATASET_ACTION_MISSING_DATASET_ID_FIELD`).
- A `bulk-dataset-ingest` action's `sourceFileField` is set and resolves to a known field — a
  captured input, or a support-system capability's own declared output
  (`_MISSING_SOURCE_FIELD`/`_INVALID_SOURCE_FIELD`).
- It declares at least one column (`_MISSING_COLUMNS`), every column has both a `key` and `title`
  (`_INVALID_COLUMN`), no two columns share a `key` (`_DUPLICATE_COLUMN_KEY`), and every
  `role`/`valueKind` is one of the closed, known vocabularies (`_UNKNOWN_ROLE`/`_UNKNOWN_VALUE_KIND`).
- Exactly one column declares `role: "RowKey"` — never zero, never more than one
  (`_MISSING_ROW_KEY`/`_DUPLICATE_ROW_KEY_ROLE`).
- A `bulk-dataset-materialize` action's `datasetIdField` matches some `bulk-dataset-ingest`
  action's own `datasetIdField` elsewhere in the blueprint (`_UNKNOWN_DATASET`), and it declares a
  `targetFileField` (`_MISSING_TARGET_FIELD`).

Separately, `ValidateDataDisplayBindings()` treats any field key declared in an ingest action's
`datasetIdField`/`errorCountField`/`warningCountField`/`acceptedCountField` as a known, legitimate
binding for a `summary-list`/`stat-group` anywhere in the blueprint — the same as a captured input
field, a `calculations.fields` entry, or a support system's own declared `Outputs`.

## Using it in a blueprint

The automation stage — entered on the initial upload's Split branch, and re-entered every time a
"revalidate" route loops back to it — carries both actions, materialize ordered before the call
that reads its output:

```json
{
  "actions": [
    {
      "type": "bulk-dataset-materialize",
      "timing": "onEnter",
      "params": {
        "datasetIdField": "contributionsDatasetId",
        "targetFileField": "contributionsFile"
      }
    },
    {
      "type": "support-system-call",
      "timing": "onEnter",
      "params": {
        "supportSystemKey": "safetynet-underwriting",
        "capabilityKey": "validate-contributions-file",
        "inputs": { "file": "contributionsFile" }
      }
    }
  ]
}
```

The review stage — reached once the join gateway releases, after SafetyNet's response resolves —
carries the ingest action:

```json
{
  "type": "bulk-dataset-ingest",
  "timing": "onEnter",
  "params": {
    "sourceFileField": "contributionsResponseFile",
    "datasetIdField": "contributionsDatasetId",
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

Placing a `BulkDataReviewComponent` on that same stage, bound to `contributionsDatasetId`, renders
the review UI against whatever the ingest action's `columns` already declared — the component
itself needs no column configuration of its own (see
[Extending the component catalog](./extending-the-component-catalog.md) for the general pattern
this follows). Its own "revalidate" route targets the original Split gateway again — re-entering
the automation stage above runs materialize (this time with a real `datasetIdField` value),
overwriting `contributionsFile` with the corrected data before `support-system-call` resubmits it.

## Authoring in the editor

Both actions get a dedicated editor in `Wayfinder.Editor.Client` (same rationale as
`support-system-call`'s own dedicated editor — see
[Extending the component catalog](./extending-the-component-catalog.md)), reachable via the
"Add action" picker as **Ingest a bulk dataset** / **Materialize a bulk dataset**. Unlike
`support-system-call`, neither action's shape depends on a value chosen while authoring it, so
the whole form — scalar field-refs and the repeatable `columns` list alike — is a single
schema-driven render against a hand-authored, static property schema, reusing the exact
recursive Array-of-Object rendering a component's own properties (e.g. a stat-group's `items`)
already get for free: no bespoke list-editing code, just "add a column" / "remove a column"
buttons and one form field per column property (`key`, `title`, `valueKind`, `format`, `role`,
`visible`, `editable`), `role` and `valueKind` rendering as closed-vocabulary selects. `columns`
is the *only* place a bulk dataset's shape is authored — the `BulkDataReviewComponent` on the
review stage needs none of its own. The editor's own live diagnostics mirror
`ValidateBulkDatasetActions()`'s checks client-side (missing/invalid `sourceFileField`, missing
`datasetIdField`, no columns, wrong `RowKey` count, a `bulk-dataset-materialize` action's
`datasetIdField` not matching any ingest action's) for an immediate nudge before Save; the
server-side check is still the authoritative one.

## Requiring explicit confirmation for a non-blocking condition

Errors and warnings mean different things: an error blocks (the route simply isn't offered until
`errorCountField` reaches zero — see [Validation](#validation) above), but a warning shouldn't
stop someone finishing who's already checked it's fine. The worked example still wants an explicit
"yes, I've seen this" moment before finishing with a warning on record, without touching the
engine at all — two routes sharing one trigger and label, gated by mutually exclusive `showWhen`
conditions, one of them routing through a small interstitial stage instead of straight to done:

```json
{
  "routes": [
    { "target": "to-done", "trigger": "accept", "label": "Accept and finish",
      "showWhen": "contributionsErrorCount = 0 and contributionsWarningCount = 0" },
    { "target": "to-confirm-warnings", "trigger": "accept-with-warnings", "label": "Accept and finish",
      "showWhen": "contributionsErrorCount = 0 and contributionsWarningCount > 0" }
  ]
}
```

The caseworker only ever sees one "Accept and finish" button — never both — since the two
`showWhen` conditions can't both be true. The interstitial stage itself just needs a route back
into the same `to-done` gateway the direct path already uses, and (optionally) a "Back to review"
escape hatch: re-entering the review stage is safe, `bulk-dataset-ingest`'s own idempotency cache
(keyed on `instanceId`/stageKey/source file) means it reuses the already-ingested dataset rather
than re-parsing. Both `to-confirm-warnings` and the "back" route need their own single-route Split
gateway each — a stage's routes must always target a gateway, never another stage directly (see
`ServiceBlueprint.ValidateGatewayRouting()`).

**A footgun worth knowing before writing a condition like the one above**: `showWhen` fails
**open** — a `CalculationException` while evaluating it leaves the route visible, not hidden
(logged as a warning, not surfaced as an error). A field populated by `errorCountField`/
`warningCountField`/`acceptedCountField` lives in `ServiceRequest.FieldValues`, but referencing it
in a `showWhen` expression still requires declaring it under the blueprint's own
`calculations.fields` block with `source: "service"` (the same "service"-sourced pattern
`errorCountField` needs, backed by a host resolver that reads the value straight back off
`FieldValues` — see `Wayfinder.ReferenceApp/Program.cs`'s own `serviceInputsResolver`). Skip that
declaration and every route conditioned on the field **stays visible regardless of its actual
value** — silently, since the failure is swallowed. `njf-contributions.json` declares both
`contributionsErrorCount` and `contributionsWarningCount` for exactly this reason.

## Corrections autosave

`BulkDataReviewComponent`'s row cards save a correction automatically (debounced, shortly after
you stop typing) rather than needing an explicit button — a manual save button meant a second edit
made after saving once could be silently left out of a later `bulk-dataset-materialize`, since
materialize always reads whatever the store last had, not whatever's currently on screen. Every
navigation away from the current page of rows (paging, filtering, or submitting any route on the
stage) flushes any still-pending save first and waits for it, so a change can't be silently
dropped by clicking away before the debounce fires.

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
