# The Wayfinder Calculation Language

A total, side-effect-free expression language for the maths behind a service blueprint,
pension quotes, eligibility thresholds, banded tax calculations, whatever a stage
needs to compute. It's the **only** place business maths should live: don't
hand-write it in a host service or a client component (see
[Umbraco.Prism's CLAUDE.md](https://github.com/jonnymuir/Umbraco.Prism/blob/main/CLAUDE.md#declarative-calculations--live-stages-money-modeller-pattern),
which documents this convention for that host).
`Wayfinder` ships two runtimes for this grammar, both checked against the same conformance
suite, [`calculation-golden.json`](../../Wayfinder/calculation-fixtures/calculation-golden.json),
if you're unsure whether something is legal syntax, that file is the ground truth:

- **C#** (`Wayfinder/Services/Calculations`), authoritative. The engine only ever persists or
  branches on what this runtime computes; nothing a client claims to have calculated is ever
  trusted for a real decision.
- **JavaScript** (`Wayfinder.Rendering.GovUk/wwwroot/js/wayfinder-calculations.js`, shipped as
  this package's own static web asset at `/_content/Wayfinder.Rendering.GovUk/js/`), for a host
  that wants the same expressions re-evaluated client-side between form submits (instant
  `showWhen`/chart updates with no round-trip). Plain ES module, no build step, no framework
  dependency, a host loads it the same way it loads `wayfinder-slider.js`. Ported from
  [Umbraco.Prism](https://github.com/jonnymuir/Umbraco.Prism)'s own independent TypeScript
  evaluator (`calculation-engine.ts`), which mirrored this same grammar in a separate repo before
  this port existed; Wayfinder is now the canonical source for both runtimes, so the two stop
  drifting independently of each other. Run its own conformance check with
  `node Wayfinder.Rendering.GovUk/test/wayfinder-calculations.conformance.mjs`.

Using the client-side runtime doesn't change the engine's trust model: it's purely a preview
accelerator for what the same submission would compute server-side. A host still only ever
submits raw field inputs (never a pre-computed result) to `Advance`, which always recomputes the
calculation scope itself from persisted `FieldValues`, the same server-side check that already
existed before any client-side runtime did.

This document is also exposed as an MCP resource (`service-blueprint-docs://calculation-language`)
so an AI agent authoring service blueprints through the MCP toolkit can fetch it directly, without
needing filesystem access to this repo, see
[AI-Ready Service Blueprint Authoring](./ai-service-blueprint-authoring.md).

**Authoring this visually:** the service blueprint editor's own **Calculations** tab
(`Wayfinder.Editor.Client/src/service-blueprint-editor/wayfinder-calculations-editor.ts`) is a
schema-driven, human-facing alternative to hand-typing this JSON, live syntax highlighting and
inline error positions (reusing this exact grammar's own tokenizer/parser, not a second
implementation), inline autocomplete for every input/field/table name (type a fragment of either
its identifier or its own label, or press Ctrl+Space to browse) instead of having to remember
exact spelling, and fully automatic field declaration ordering (see
[Where it lives in a service blueprint](#where-it-lives-in-a-service-blueprint) below on why
declaration order matters), a field is never asked to be manually reordered, and a genuine
circular dependency is caught and named immediately. It writes the exact same JSON described in
this document; nothing below is specific to either authoring surface.

## Where it lives in a service blueprint

A `ServiceBlueprint` may carry a top-level `calculations` block:

```json
"calculations": {
  "tables": { "<tableName>": { "interpolate": "linear", "values": { "<key>": <number>, ... } } },
  "fields":  { "<fieldName>": { "expr": "<expression>" } },
  "series":  { "<seriesName>": { "over": "<loopVar>", "from": "<expr>", "to": "<expr>", "values": { "<column>": "<expression>" } } }
}
```

- **`fields`** are evaluated once, in declaration order, into a shared scope. Each
  field's expression can reference any input field, any earlier-declared field, or a
  service-sourced value, but not a field declared later (forward references are an
  error).
- **`tables`** are static lookup tables consumed via the `lookup()` function.
- **`series`** repeat a set of expressions across a range of an integer loop variable
  (e.g. "projected income for every age 66 to 90"), the standard way to drive a
  `chart` component.

Every input component (`number`, `decimal`, `slider`, `boolean`, etc.) is
automatically in scope under its `fieldKey`, typed as `decimal` for numeric field
types, `boolean` for a `boolean` field, and `string` for everything else
(`CalculationScopeBuilder`), seeded from the submitted value or the component's own
`default` if nothing's been submitted yet. Any component may also declare a
`showWhen` expression (a plain string in this same language) to control its own
visibility, see [Visibility (`showWhen`)](#visibility-showwhen) below.

**`validate_service_blueprint` has no submitted values to work with**, so it can
only seed scope from each input's own `default`. A `string`/`boolean` field with no
declared default still resolves, an unfilled text box already means `""` and an
unticked checkbox already means `false` everywhere else in this system, so
`CalculationScopeBuilder` gives it that same safe placeholder rather than treating
it as unknown. A `number` field is the one case with no equally safe placeholder
(`0` is a real, meaningful value a service might act on, not a stand-in for
"nothing submitted yet"), so it's genuinely absent from scope with no default,
any expression referencing it bare then fails static evaluation. This surfaces as a
`Warning`-severity diagnostic (see [Errors](#errors)), not a blocking error: it's an
expected limit of static validation, not an authoring mistake. Two
ways to verify the expression anyway: give the numeric input a sensible `default`
(recommended, it also seeds the real form), or verify via
`simulate_service_blueprint` instead, which takes real `fieldValues` per step and
resolves cleanly regardless of defaults.

## Grammar

Lowest to highest precedence:

```
or             ::= and ( "or" and )*
and            ::= not ( "and" not )*
not            ::= "not" not | comparison
comparison     ::= additive ( ("=" | "<>" | "<" | "<=" | ">" | ">=") additive )?
additive       ::= multiplicative ( ("+" | "-") multiplicative )*
multiplicative ::= unary ( ("*" | "/") unary )*
unary          ::= "-" unary | primary
primary        ::= number | string | "true" | "false" | identifier-path
                  | identifier "(" args ")" | "(" or ")"
```

That's the entire language, no assignment, no loops (outside the declarative
`series` construct), no array indexing, no arbitrary method calls. Every expression
is guaranteed to terminate.

- **Numbers** are invariant-culture decimals: `55`, `0.1`, `74208`. No thousands
  separators, no currency symbols in the expression itself, those belong on the
  *input component* (`prefix: "£"`), stripped before the value reaches the scope.
- **Strings** use single quotes: `'Maximum tax-free cash'`.
- **Booleans** are the bare identifiers `true`/`false`.
- **Identifier paths** are dotted for nested (service-sourced) values:
  `member.age`, `member.active`. A bare identifier resolves an input field, an
  earlier calculated field, or a series loop variable.
- Comparison, `and`/`or`/`not` all read as plain English. `and`/`or` **short-circuit**,
`false and (1 / 0 > 0)` evaluates to `false` without ever touching the division.
- Operator precedence is standard (`*`/`/` bind tighter than `+`/`-`; comparisons
  bind tighter than `and`; `and` binds tighter than `or`); use parentheses to
  override it, as usual.

## Numeric semantics

All arithmetic is C# `decimal` / an equivalent fixed-point type client-side, **not**
floating point, so `0.1 + 0.2 = 0.3` exactly, no drift. The one exception is `pow()`,
which round-trips through `double` because fractional exponents have no exact
decimal representation in any base. If a `pow()` result needs to match exactly
between runtimes or feed into a currency display, wrap it in `round()`.

## Functions

| Function | Signature | Behaviour |
|---|---|---|
| `if` | `if(cond, then, else)` | Ternary. `cond` must evaluate to a boolean. |
| `min` / `max` | `min(a, b, ...)` (2+ args) | Numeric min/max across all arguments. |
| `clamp` | `clamp(value, lo, hi)` | Equivalent to `min(max(value, lo), hi)`. |
| `abs` | `abs(x)` | Absolute value. |
| `floor` | `floor(x)` | Round toward negative infinity. |
| `round` | `round(x)` or `round(x, places)` | Rounds **away from zero** at the midpoint (`round(2.5) = 3`, `round(-2.5) = -3`), not banker's rounding. `places` defaults to `0`. |
| `pow` | `pow(x, y)` | `x` raised to the power `y`. Round-trips through `double`, see [Numeric semantics](#numeric-semantics). |
| `lookup` | `lookup(tableName, key)` | Looks up `key` in a `tables` entry. The first argument must be a bare table name, not an expression. See [Tables and `lookup()`](#tables-and-lookup). |
| `matches` | `matches(text, pattern)` | Regex predicate, `true` if `pattern` matches anywhere in `text`, `false` otherwise. Both arguments must be strings. `pattern` is always author-supplied (blueprint content, never end-user input), so there's no injection surface from a submission; a pathological pattern is bounded by a 100ms timeout in the authoritative C# runtime and fails the expression (`CalculationException`) rather than hanging. The JS preview runtime has no such timeout, harmless there, since (as above) it's never trusted for a real decision, only re-verified server-side on submit. An invalid pattern is also a `CalculationException`. |

Calling an unknown function, or calling a known one with the wrong argument count,
is a `CalculationException`, see [Errors](#errors).

## Tables and `lookup()`

```json
"tables": {
  "pensionAgeFactor": {
    "interpolate": "linear",
    "values": { "55": 0.56, "66": 1.0, "75": 1.27 }
  }
}
```

- `values` keys are decimal strings (parsed as the lookup key), values are numbers.
- `interpolate` is `"linear"` (default) or `"step"`.
  - **Linear**: interpolates between the two nearest keys,
    `lookup(pensionAgeFactor, 60)` with the table above gives `0.56 + (1.0 - 0.56) * (60 - 55) / (66 - 55) ≈ 0.76`.
  - **Step**: holds the *lower* key's value until the next key is reached exactly,
    `lookup(band, 99)` on `{"0": 10, "100": 20}` returns `10`, not `20`, until the key
    hits `100`.
- A key **outside** the table's range clamps to the nearest edge value rather than
  erroring, `lookup(factor, 90)` on a table whose highest key is `75` returns the
  value at `75`.
- An unknown table name, or an empty table, is a `CalculationException`.

## Series

```json
"series": {
  "incomeByAge": {
    "over": "age",
    "from": "retireAgeEff",
    "to": "90",
    "values": {
      "db": "round(pensionOut)",
      "sp": "if(age >= statePensionAge, round(statePension), 0)"
    }
  }
}
```

Produces one row per integer step from `from` to `to` inclusive, with the loop
variable (`age` here) in scope for every `values` expression alongside every field
and input. `from`/`to` are themselves expressions (evaluated once, must resolve to
whole numbers), so a series range can depend on a calculated field, as above
(`retireAgeEff`). Capped at 1000 rows; a series that would produce more is a
`CalculationException`. If `to < from`, the series is simply empty, not an error.
The loop variable name must not collide with an existing input or field name.

Series are the data source for `chart` components, bind a chart's `series` property
to the series name, and its `x`/band keys to columns declared in `values`.

## Visibility (`showWhen`)

Any component (not just inputs, headings, warning banners, whole fieldsets) may
carry a `showWhen` string in this same expression language, evaluated against the
same scope as `calculations` (inputs, service inputs, calculated fields). A few real
examples from [`money-modeller.json`](https://github.com/jonnymuir/Umbraco.Prism/blob/main/src/UmbracoPrism.MockBusinessApp/service-blueprints/money-modeller.json)
(Umbraco.Prism's worked example, see below):

```json
{ "type": "warning-text", "showWhen": "not quoteMode and retireAge < npa", "content": "..." }
{ "type": "slider", "showWhen": "not quoteMode and hasDc", "fieldKey": "invReturn", ... }
```

When it evaluates to `false`, the component renders hidden; the client-side
live-form runtime re-evaluates it as the user changes inputs, so visibility updates
without a full page round-trip. `showWhen` must evaluate to a boolean, a `showWhen`
that evaluates to a number or string is a `CalculationException`, same as any other
type mismatch.

## Route visibility (`showWhen` on a route)

A `StageDefinition`'s own routes (`routes[]`) may carry the identical `showWhen`
string, same expression language, same scope, same fail-open evaluation as a
component's `showWhen`. When it evaluates to `false`, that route is excluded from
the stage's available actions entirely, not rendered as a disabled button, not
offered at all, and submitting its trigger anyway is rejected exactly like
submitting an action that was never declared:

```json
"routes": [
  { "id": "review--send-to-insurer", "target": "to-insurer-check", "trigger": "send-to-insurer",
    "label": "Send risk assessment to insurer", "showWhen": "riskAssessment <> ''" },
  { "id": "review--continue", "target": "to-decision", "trigger": "continue",
    "label": "Continue to decision", "showWhen": "riskAssessment = ''" }
]
```

Only one of these two buttons is ever rendered, depending on whether a risk
assessment was actually attached, the real shape used by
`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`'s `under-review`
stage (see [Support systems](./support-systems.md)).

**Route `showWhen` vs. a scoped stage validation rule**, the two look similar but
answer different questions, and picking the wrong one produces a worse UX than
either alone:

- Use route `showWhen` when a stage has genuinely different exits and exactly one
  should be *offered* for a given state of the data. The example above: an
  applicant either did or didn't attach a file, so exactly one of "send to
  insurer" / "continue" makes sense to show at all. Offering both and rejecting
  the wrong one after the fact just makes the caseworker guess.
- Use a [stage validation](#stage-validations) rule, scoped via `actions`, when
  the exit should *always stay visible* but needs to be blocked with an
  explanation until something holds, e.g. an "Approve" button that's always on
  screen, but refuses with a message until a checklist is complete. Hiding
  "Approve" entirely there would be worse: the caseworker wouldn't know the
  option exists or why it's missing.

**Only meaningful on a stage's own routes.** `showWhen` has no effect on a
gateway's own routes: a Split gateway always fans out to every outgoing route
regardless (that's what makes the multi-cursor Join model work), and a Join
gateway selects its one outgoing route by matching the arriving trigger, not by
evaluating anything. `save_service_blueprint`/`validate_service_blueprint` flags
a `showWhen` set on a gateway route with a warning (`ROUTE_SHOW_WHEN_ON_GATEWAY_ROUTE`)
rather than let it silently do nothing.

## Stage validations

A `StageDefinition` may carry a `validations` list, declarative, cross-field business rules
checked before that stage is allowed to advance. This is the engine-native alternative to a host
overriding `ProcessManagerEngine.ValidateAdvance` with bespoke C#: the rule lives in the blueprint
itself, visible to the editor, to `validate_service_blueprint`, and to anyone reading the JSON,
instead of only existing as compiled code in a host repository.

```json
"stages": [
  {
    "stageKey": "risk-assessment",
    "validations": [
      {
        "code": "risk-mitigation-evidence-required",
        "when": "hasDangerousProps",
        "rule": "riskAssessment <> '' or mitigationHasEvidence",
        "field": "riskMitigationNotes",
        "message": "Attach a risk assessment above, or describe your mitigation with a measurable detail — a safety distance in metres, or a recognised body (HSE, IOSH, NOABA)."
      }
    ]
  }
]
```

- **`code`**, machine-readable identifier, surfaced on the resulting problem's `code` (e.g. so a
  host can route on it).
- **`when`** (optional), a guard expression; must evaluate to a boolean. When present and
  evaluates to `false`, this rule is skipped entirely, no failure, not evaluated. Kept separate
  from `rule` deliberately, rather than folded into a single implication (`not (when) or rule`):
  an author only ever writes an affirmative "what must hold", never has to hand-negate an
  applicability condition (a common, hard-to-spot class of authoring bug) and an editor or
  `validate_service_blueprint` gets a distinct expression to reason about "does this rule even
  apply" separately from "is it satisfied". Omit for a rule that always applies.
- **`rule`**, must evaluate to `true` for the stage to be allowed to advance. Positive phrasing
  only, matching `showWhen`'s convention, there is no separate "failWhen".
- **`field`** (optional), a fieldKey on this same stage to attach the failure to, GDS
  error-summary style. Omit for a stage-level problem not tied to one field.
- **`message`**, shown to the user when the rule fails.
- **`actions`** (optional), the action keys (route triggers) this rule guards. Omit, the
  default, and it guards *every* way out of the stage, which is what a data-completeness rule
  wants ("these answers must be consistent before you leave, however you leave"). Name actions
  when a stage offers genuinely different exits and only some of them require the rule. The case
  this exists for: a caseworker review stage where "send to the insurer" stays available but
  "continue without sending" is refused once the applicant has actually attached something the
  insurer needs to see, an unscoped rule would block the very action it is trying to force. It
  lists the actions the rule *guards*, not the ones it exempts, so adding a new exit to a stage
  later can never silently inherit a rule that was never written for it. See
  [Support systems § Making a support-system call mandatory](./support-systems.md#making-a-support-system-call-mandatory).

Both `when` and `rule` are ordinary expressions in this same language, evaluated against the
identical scope `calculations.fields`/`showWhen` already use, which spans every stage the
instance has reached, not just the one the rule is declared on. In the example above,
`hasDangerousProps` is captured on an earlier stage (`event-details`) than the rule that reads it
(`risk-assessment`), no extra wiring is needed for a rule to reference a field captured earlier
in the journey. `mitigationHasEvidence` is an ordinary calculated field using `matches()`:

```json
"calculations": {
  "fields": {
    "mitigationHasEvidence": {
      "expr": "matches(riskMitigationNotes, '\\b\\d+\\s?(m|metres|meters)\\b') or matches(riskMitigationNotes, '\\b(HSE|IOSH|NOABA)\\b')"
    }
  }
}
```

See the real, worked version of this in
[`juggling-licence.json`](https://github.com/jonnymuir/Wayfinder/blob/main/Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json)
(the `risk-assessment` stage), Wayfinder's own reference app, not an external example this time.

**Evaluation is server-side only, and biased toward blocking.** `ProcessManagerEngine.Advance`
evaluates `validations` after field-level validation passes, on the merge of persisted and
just-submitted values, never on stale data, never on anything a client claims was already
validated. Unlike `showWhen` (a display hint, tolerant of any non-`false` result), this is a hard
gate: a `when` that doesn't evaluate to exactly `false` is treated as applying, a `rule` that
doesn't evaluate to exactly `true` is treated as failed, and a rule whose expressions throw
(`CalculationException`) is treated as failed rather than skipped. A calculated field that fails
only affects the specific rules that actually reference it, not every validation on the stage.
This should be rare in practice, `validate_service_blueprint` already statically checks every
`when`/`rule` before a blueprint can be saved, including that the result is genuinely a boolean
(a rule that evaluates cleanly to a number or string is flagged there too, since the engine would
otherwise silently treat it as "not exactly true" and fail on every submission).

**Authoring this visually:** the Calculations tab's Validations section groups rules by the stage
they're declared on, reuses the same expression editor (with its inline autocomplete)
`calculations.fields` gets, and offers a dropdown of the owning stage's own fieldKeys for `field`,
nothing here asks an author to remember exact field spelling or hand-write the grammar.

## Service-sourced fields

A field with no `expr` and `"source": "service"` instead is supplied by the *host
application*, not computed:

```json
"fields": { "member": { "source": "service" } }
```

The host implements `ProcessManagerEngine.ResolveServiceInputs(...)` to supply it
(e.g. a member record fetched from a system of record), see
[`BusinessAppProcessManager.ResolveServiceInputs`](https://github.com/jonnymuir/Umbraco.Prism/blob/main/src/UmbracoPrism.MockBusinessApp/Services/BusinessAppProcessManager.cs)
in Umbraco.Prism for the real example backing `money-modeller.json`'s `member` field. A service field with
no value supplied is a `CalculationException` when evaluated for real; the
`validate_service_blueprint`/`simulate_service_blueprint` MCP tools have specific, non-fatal handling
for this case, see [AI-Ready Service Blueprint Authoring](./ai-service-blueprint-authoring.md).

### `valueKind` and `default` (authoring-time only)

Static validation has no real value for a service field, so it can't check any
`showWhen`, calculated field or stage rule that reads one, and reports each such
field as `CALC_SERVICE_FIELD_UNVERIFIED` (a **Warning**, never blocking). Two
optional properties let you close that gap for a scalar value:

```json
"contributionsErrorCount": { "source": "service", "valueKind": "number", "default": "0" }
```

- **`valueKind`** — `"number"`, `"string"` or `"boolean"`. With a kind declared,
  validation gives a `"string"`/`"boolean"` field the same safe placeholder (`""` /
  `false`) an unfilled input of that kind already gets, and every expression that
  reads it is checked normally. A `"number"` has no safe placeholder (`0` is a real
  value), so it *also* needs `default`.
- **`default`** — a string, parsed per `valueKind` (`"0"` is the number zero). A
  stand-in for validation only; it is **never** a runtime fallback. If the host's
  resolver fails to supply the value at render time that is still a
  `CalculationException`, not silently papered over.

Omit both for a value with no scalar kind (an object handed back whole, like
`member` above) — it stays unverifiable, and the Warning is expected. Structural
mistakes are errors: a `valueKind` outside the three values, a `default` with no
`valueKind` to parse it, a `default` that doesn't parse, or either property on a
non-`service` field.

## Format hints

A field may declare `"format": "gbp"` to tell the *rendering* layer (not the
calculation itself) to display it as currency, the calculated value stays a plain
`decimal` in the scope; formatting is a display concern applied when the value is
bound into a `stat-group` item, `summary-list` row, or similar.

## Errors

Every parse or evaluation failure throws `CalculationException` with a message
carrying enough context to act on, position for parse errors
(`"Unexpected 'x' at position 12 in: 1 + x"`), field/series/expression name for
evaluation errors (`"Field 'x' has no expression and no service source."`,
`"Unknown name 'y' in expression 'x'."`). Common cases:

| Situation | Example message |
|---|---|
| Unknown identifier | `Unknown name 'nosuchthing' in expression '...'.` |
| Unknown function | `Unknown function 'eval' in ...` |
| Wrong argument count | `clamp() expects 3 argument(s), got 2, in ...` |
| Type mismatch | `Expected a number but got 'true' in ...` |
| Division by zero | `Division by zero in ...` |
| Forward field reference | `Unknown name 'b' in expression 'b + 1'.` (fields only see earlier-declared fields) |
| Field name collides with an input or earlier field | `Field 'x' collides with an input or earlier field.` |
| Unresolved service field | `Field 'member' is service-sourced but the host did not supply it.` |
| Unknown table | `Unknown table 'foo' in ...` |
| Invalid regex pattern to `matches()` | `matches() has an invalid pattern '(' in ...` |
| Series too large | `Series 's' would produce 2000 rows; the limit is 1000.` |

When authoring through the MCP toolkit, `validate_service_blueprint` surfaces these against
the whole service blueprint (calculated fields, series, **and** every component's `showWhen`)
as structured diagnostics you can act on directly, rather than needing to run the
service blueprint to discover a broken expression, see
[AI-Ready Service Blueprint Authoring](./ai-service-blueprint-authoring.md).

One class of `Unknown name` is downgraded from `Error` to `Warning` rather than
blocking the save: an expression referencing a `number`-typed input with no
declared default (see the callout above), or a `source: "service"` field with no
`valueKind`/`default` to stand in for the host's value (see *Service-sourced
fields*). Both say plainly that they're a limit of static checking, not a broken
expression, and name `simulate_service_blueprint` as the way to verify the
expression with real values instead. Neither ever stops the rest of the
validation pass: a genuine mistake in another expression in the same service
blueprint is still reported in the same run.

## Worked example: `money-modeller.json`

Wayfinder's own repo doesn't ship a calculation-heavy demo blueprint, the reference app
in this repo (see [Reference App](./reference-app.md)) is deliberately kept simple. The
fullest worked example of this language lives in a real deployed consumer,
[Umbraco.Prism](https://github.com/jonnymuir/Umbraco.Prism):
[`money-modeller.json`](https://github.com/jonnymuir/Umbraco.Prism/blob/main/src/UmbracoPrism.MockBusinessApp/service-blueprints/money-modeller.json),
a pension modeller. Its field chain is worth reading top-to-bottom as a model for
how to structure a non-trivial calculation set: each field builds on the last, so
the dependency order *is* the declaration order.

1. **`member`**, `source: "service"`, the host-supplied member record (name, age,
   salary, accrued benefits).
2. **`quoteMode`** (`qPension > 0`) and **`todaysMoney`**, booleans that gate large
   parts of the rest of the calculation and several components' `showWhen`.
3. **`minRetireAge`** (`max(55, member.age + 1)`) / **`maxRetireAge`**, bounds fed
   into `retireAgeEff` via `clamp()`.
4. **`retireAgeEff`**, `clamp(if(quoteMode, qAge, retireAge), minRetireAge, maxRetireAge)`,
   the single effective retirement age used everywhere downstream, whether the
   member is modelling freely or matching a formal quote.
5. **`futurePension`**, **`basePension`**, **`baseLump`**, **`pot`**, the projection
   maths, using `pow()` for compound growth (`pow(1 + realReturn, years)`) and
   `if(member.active and not quoteMode, ..., 0)` to gate projection entirely when a
   member has already left or is working from a fixed quote.
6. **`pensionFactor`** / **`lumpFactor`**, `lookup()` against the `pensionAgeFactor`
   / `lumpAgeFactor` tables, applying the early/late retirement adjustment.
7. **`adjPension`**, **`adjLump`**, **`adjPot`**, **`totalValue`**, the age- and
   inflation-adjusted final figures, and the total pot value used to work out the
   maximum tax-free cash (`maxTfc = 0.25 * totalValue`).
8. **`resultPension`**, **`resultCash`**, **`resultDcIncome`**, **`resultTotal`**,
   the final `round(...)`-ed, `format: "gbp"` fields bound directly to the
   `stat-group` component on the `model` stage.
9. The **`incomeByAge`** series projects `resultPension`-style figures across every
   age from `retireAgeEff` to `90`, feeding the `chart` component.

Fetch the full file to see every field's exact expression, this is the reference
to copy the *shape* of a real calculation set from, not to reproduce verbatim.
