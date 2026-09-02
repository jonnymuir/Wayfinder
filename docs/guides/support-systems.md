# Support systems

How Wayfinder models Nielsen Norman Group's third service-blueprint lane, the
external/downstream systems a backstage actor calls out to, and how a toolkit integrator
registers one. This is for whoever is *building* a Wayfinder host, not whoever is *authoring* a
service blueprint against one; if that's you, see
[Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) instead.

This document is also exposed as an MCP resource (`service-blueprint-docs://support-systems`)
so an agent can fetch it directly without repo access.

---

## Where this sits in the service-blueprint model

NN/g's article (nngroup.com/articles/service-blueprints-definition) defines **support
processes** as "internal steps, and interactions that support the employees in delivering the
service", credit-card verification, pricing, quality testing are its own examples. Wayfinder's
reference app already models two of the five NN/g layers as queues: a citizen-facing
(frontstage) queue and a caseworker-facing (backstage) queue. A **support system** is the third:
an external, API-driven actor a caseworker's own stage calls out to, and potentially waits on,
before finishing their own decision.

The worked example in this repo (`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`):
a caseworker reviewing a juggling-event licence application sends the applicant's uploaded risk
assessment to **SafetyNet Underwriting**, a fictional insurer, and waits for its approve/reject
decision before finishing their own review.

## The abstraction: capability-declared completion mode

A support system doesn't get bespoke engine code. It's a **descriptor**: what the system is
called and what it can do, plus a small client the host registers alongside it. Two things stay
deliberately generic rather than baked into any one integration:

- **Inputs** a capability needs are declared using `ComponentPropertyDescriptor`, the exact
  same recursive shape already shared with action parameters and component properties (see
  [Extending the component catalog](./extending-the-component-catalog.md)). An input tagged
  `Format: "field-ref"` gets the existing reference-aware field-ref editor machinery for free,
  no bespoke input-authoring UI. A capability can declare more than one input.
- **How the outcome comes back**: poll or webhook, is declared per capability, not assumed by
  the engine. The engine always offers *both* mechanisms as generic, always-on plumbing: a
  poll-check hook (invoked whenever a client re-polls a waiting stage, reusing the exact
  defer/poll envelope a join gateway already returns) and a generic webhook receiver (resolving
  an opaque invocation id back to the pending cursor). A capability's
  `SupportedCompletionModes` tells the engine which of those two it should actually use for that
  capability, the engine only calls the client's status-check method if `Poll` is declared, and
  only hands the client a callback URL if `Webhook` is declared. A capability can declare both,
  as SafetyNet Underwriting's does, to demonstrate both paths genuinely resolving the same call.

This means adding a new support system with a different completion strategy, synchronous-only,
webhook-only, poll-only, needs no engine change, only a new descriptor and a new client.

## `SupportSystemDescriptor` reference

| Field | Meaning |
|---|---|
| `Key` | Unique across the whole process, a duplicate throws at registration time. |
| `DisplayName` | Human-readable name, e.g. "SafetyNet Underwriting". |
| `Description` | Longer help text, editor tooltip / AI-agent-readable prose. |
| `Capabilities` | `IReadOnlyList<SupportSystemCapabilityDescriptor>`, see below. Must be registered with at least the capabilities a blueprint will reference. |

## `SupportSystemCapabilityDescriptor` reference

| Field | Meaning |
|---|---|
| `Key` | Unique within the support system, e.g. `"validate-risk-assessment"`. |
| `DisplayName` | Human-readable name for editor UI. |
| `Inputs` | `IReadOnlyList<ComponentPropertyDescriptor>`, what this capability needs, reusing the exact same property-descriptor shape as a component's own properties. |
| `Outputs` | `IReadOnlyList<ComponentPropertyDescriptor>`, blueprint field keys this capability's resolution writes directly into instance state once it resolves (e.g. a decision note). A `summary-list`/`stat-group` elsewhere in the blueprint may legitimately bind to one of these even though no stage ever captures it as an input, see [Validation](#validation) below. Unlike `Inputs`, there's no per-blueprint remapping: the `Key` declared here is exactly the field key a host's `ISupportSystemClient` is expected to write. |
| `SupportedCompletionModes` | `Poll` and/or `Webhook`, must declare at least one, or an invocation could never resolve. |
| `Outcomes` | The closed set of decisions this capability can resolve to, e.g. `approved`/`rejected`, must declare at least one. A blueprint's outgoing routes from the calling stage are validated against this vocabulary. |

## Validation

Registering a descriptor gets a blueprint referencing it real, comprehensive validation for
free, `ServiceBlueprint.ValidateSupportSystemActions()` (surfaced through
`validate_service_blueprint`/`save_service_blueprint`, the same as
[component property validation](./extending-the-component-catalog.md#validation-comes-for-free)):

- `supportSystemKey`/`capabilityKey` are present and actually registered
  (`SUPPORT_SYSTEM_ACTION_MISSING_KEYS`/`_UNKNOWN_SUPPORT_SYSTEM`/`_UNKNOWN_CAPABILITY`).
- Every input the capability declares `Required` is bound in the action's own `params.inputs`
  (`SUPPORT_SYSTEM_ACTION_MISSING_REQUIRED_INPUT`), and every key in `params.inputs` actually
  names a declared input (`SUPPORT_SYSTEM_ACTION_UNKNOWN_INPUT`, catches a typo).
- Each bound input's blueprint field key actually exists somewhere in the blueprint
  (`SUPPORT_SYSTEM_ACTION_INPUT_UNKNOWN_FIELD`).
- The carrying stage's own outgoing route triggers are all outcomes the capability can actually
  resolve to (`SUPPORT_SYSTEM_ACTION_ROUTE_TRIGGER_UNKNOWN_OUTCOME`), a route whose trigger isn't
  a declared outcome can never fire, since `ResolveSupportSystemOutcome` only ever delivers one.

Separately, `ValidateDataDisplayBindings()` treats every field key declared in a referenced
capability's `Outputs` as a known, legitimate binding for a `summary-list`/`stat-group` anywhere
in the blueprint, the same as a captured input field or a `calculations.fields` entry, so
showing a support system's decision on a later stage doesn't trip `DATA_DISPLAY_UNKNOWN_FIELD`.

## Registering a support system

```csharp
SupportSystemRegistry.Register(new SupportSystemDescriptor
{
    Key = "safetynet-underwriting",
    DisplayName = "SafetyNet Underwriting",
    Capabilities =
    [
        new SupportSystemCapabilityDescriptor
        {
            Key = "validate-risk-assessment",
            DisplayName = "Validate a risk assessment",
            Inputs =
            [
                new() { Key = "file", Title = "Risk assessment file", ValueKind = ComponentPropertyValueKind.String, Format = "field-ref", Required = true },
            ],
            Outputs =
            [
                new() { Key = "insurerDecisionNotes", Title = "Insurer decision notes", ValueKind = ComponentPropertyValueKind.String },
            ],
            SupportedCompletionModes = [SupportSystemCompletionMode.Poll, SupportSystemCompletionMode.Webhook],
            Outcomes =
            [
                new() { Key = "approved", DisplayName = "Approved" },
                new() { Key = "rejected", DisplayName = "Rejected" },
            ],
        },
    ],
});
```

Call this **once, at host startup, before any blueprint referencing it is read, validated, or
run**, `SupportSystemRegistry` freezes the first time anything actually reads it (`.All`,
`.Find`, `.FindCapability`), and `Register` throws after that, loudly, the same registration
discipline `ComponentTypeRegistry` already enforces (see
[Extending the component catalog § Registration timing](./extending-the-component-catalog.md#registration-timing-the-registry-freezes)).

Registering the descriptor alone gets you validation and editor authoring support for
referencing the capability. Actually calling out to the real external system is a separate
`ISupportSystemClient` implementation the host also registers alongside it, see
`Wayfinder.Engine/Abstractions/ISupportSystemClient.cs`. `ProcessManagerEngine`'s constructor
takes an `IEnumerable<ISupportSystemClient>`, keyed internally by
`ISupportSystemClient.SupportSystemKey`.

**`Inputs`/`Outputs` keys must start lowercase.** Found live, the hard way: unlike a component's
own `ComponentPropertyDescriptor.Key` values (always a real CLR property name passed via
`nameof`, so the wire converter lowercasing the first letter is a deliberate, harmless
PascalCase→camelCase translation), a capability's `Inputs`/`Outputs` keys are arbitrary,
author-chosen identifiers with no backing CLR property, but they reuse the exact same
`ComponentPropertyDescriptor` type, so the exact same converter still runs. A PascalCase key here
(the natural instinct, since `nameof`-style PascalCase is the convention everywhere else in this
toolkit) silently becomes a different string over the wire: the editor's live-fetched catalog and
a blueprint's own `params.inputs`/`params.outputs` mapping keys stop agreeing, and every reference
fails validation with no clue why. `SupportSystemRegistry.Register` now rejects an uppercase-first
key at registration time with a message explaining this, if you hit it, just lowercase the key's
first letter; nothing else needs to change.

## Registering one from configuration alone (no C#)

The common case, a support system reached by POSTing an invocation to a URL and getting the
outcome back on a callback, needs no bespoke `ISupportSystemClient` at all. It is a plain
outbound-webhook contract, and an [Umbraco Automate](https://docs.umbraco.com/umbraco-automate)
automation, Zapier, Make, n8n, Power Automate or a small service all satisfy it identically.
`AddConfiguredSupportSystems(IConfiguration)` (`Wayfinder.Engine`) reads a
`Wayfinder:SupportSystems` section and, per entry, registers both the `SupportSystemDescriptor`
and a keyed `WebhookSupportSystemClient`:

```jsonc
"Wayfinder": {
  "SupportSystems": [{
    "key": "njf-coaching-standards",
    "displayName": "NJF Coaching Standards",
    "endpoint": {
      "url": "https://your-host/umbraco/automate/webhook/<automation-guid>",
      "auth": { "type": "hmac-sha256", "secretRef": "NJF_STANDARDS_SIGNING_KEY" },
      "callbackSecretRef": "NJF_STANDARDS_CALLBACK_SECRET"
    },
    "capabilities": [{
      "key": "check-coaching-standards",
      "displayName": "Check coaching standards",
      "completionModes": [ "Webhook" ],
      "inputs": [
        { "key": "applicantName", "title": "Applicant name", "valueKind": "String", "format": "field-ref", "required": true },
        { "key": "yearsCoaching", "title": "Years coaching", "valueKind": "Integer", "format": "field-ref" }
      ],
      "outputs": [ { "key": "coachingStandardsNote", "title": "Standards note", "valueKind": "String" } ],
      "outcomes": [
        { "key": "accredited",  "displayName": "Accredited"  },
        { "key": "provisional", "displayName": "Provisional" },
        { "key": "referred",    "displayName": "Referred"    }
      ]
    }]
  }]
}
```

- `endpoint.auth.type` is `hmac-sha256` (preferred, header `X-Webhook-Signature: sha256=<hex>`
  of HMAC-SHA256 over the raw body), `header` (a plain shared secret in `X-Webhook-Secret`), or
  `none` (trusted network only, logs a warning). Both header defaults match Umbraco Automate's
  built-in webhook authenticators exactly.
- Every `*SecretRef` is the **name of a configuration key** (env var or user-secret), never the
  secret itself (no secrets in committed config).
- The POSTed envelope is `{ invocationId, instanceId, supportSystemKey, capabilityKey, inputs{…} }`.
  It deliberately carries **no callback URL**. The consumer owns its own callback target as its
  own configuration. A caller-supplied callback URL would let anyone reaching the endpoint (say,
  with a leaked signing key) turn the host into an HTTP client aimed anywhere.
- **Scalar inputs only.** A `file-upload`-backed input throws. That needs a bespoke client that
  reads bytes via `IServiceRequestFileStorage` (see `SafetyNetUnderwritingClient` in
  `Wayfinder.ReferenceApp`).

Call `AddConfiguredSupportSystems` at host startup (it registers descriptors synchronously, so
before the engine reads any blueprint); it is a no-op when the section is absent and idempotent.

## Delivering the outcome

`ProcessManagerEngine.ResolveSupportSystemOutcome(invocationId, outcomeKey, resultPayload?)` is
the one method that actually advances a waiting automation-queue cursor once a capability's
outcome is known, the single code path both delivery mechanisms end up calling:

- **Poll**: entirely automatic. Every time a client re-polls a waiting join gateway (the normal
  `GetCurrent` a caseworker's own browser already does behind the scenes), the engine checks any
  still-pending invocation blocking that gateway whose capability declared `Poll` support, calls
  the client's `CheckStatusAsync`, and calls `ResolveSupportSystemOutcome` itself if it got an
  answer. Nothing for a host to wire up.
- **Webhook**: a host's own job, the same way `GetCurrent`/`Advance` themselves are, this
  toolkit's authoring surface (`Wayfinder.Engine.Api`) is deliberately scoped to blueprint
  *authoring* only, not runtime request handling, so it doesn't ship a webhook route itself.
  `Wayfinder.Engine.Http` provides the route as a helper, mapped against your
  `ProcessManagerEngine` instance:

  ```csharp
  // sharedSecret is REQUIRED in practice. The callback endpoint denies by default when it is
  // set (checks X-Webhook-Secret in fixed time). Pass the resolved value of the entry's
  // endpoint.callbackSecretRef. Omit it only when the route is unreachable from outside a
  // trusted network (it logs a warning).
  //
  // Pass an accessor, not an instance, when the engine's own construction reads a database (an
  // Umbraco host loads blueprint definitions in its constructor) — resolving it eagerly in
  // Program.cs runs before the host's schema migrations. A plain `engine` instance is fine for a
  // host that builds the engine after its store is ready.
  app.MapWebhookSupportSystemCallbacks(
      () => app.Services.GetRequiredService<ProcessManagerEngine>(),
      sharedSecret: builder.Configuration["NJF_STANDARDS_CALLBACK_SECRET"]);
  ```

  It maps `POST /wayfinder/support-systems/callbacks/{invocationId}` binding
  `{ outcomeKey, resultPayload? }`, and returns 200 on resolution, 200 `no-op` for an unknown or
  already-resolved invocation (so a retrying caller does not storm the route), 400 for an
  undeclared outcome key, 401 for a missing or invalid secret. To hand-roll it instead, call
  `engine.ResolveSupportSystemOutcome(invocationId, outcomeKey, resultPayload)` directly.

  `invocationId` is an unguessable per-invocation token generated when the capability's onEnter
  action ran (`SupportSystemInvocationContext.InvocationId`, handed to the client's `InvokeAsync`).
  It is defence-in-depth on top of the shared secret, not the gate. It can appear in logs and in
  the consumer's run history.

A capability declaring both modes (SafetyNet Underwriting's does) may have both resolve the same
invocation, `ResolveSupportSystemOutcome` marks an invocation resolved before advancing anything,
so a second delivery for an already-resolved invocation is a safe no-op, not a double-advance.

## Using it in a blueprint

An action of type `support-system-call` on a stage or route is how a blueprint calls out to a
registered capability:

```json
{
  "type": "support-system-call",
  "timing": "onEnter",
  "params": {
    "supportSystemKey": "safetynet-underwriting",
    "capabilityKey": "validate-risk-assessment",
    "inputs": { "file": "riskAssessment" }
  }
}
```

The stage carrying this action then sits waiting, using the same `waitingContent`/
`waitingPollIntervalMs`/`requiredIncomingQueues` fields a join gateway already uses for the
citizen's "waiting behind the line of visibility" screen, until the capability resolves to one
of its declared `Outcomes`, at which point the matching outgoing route (`approved`/`rejected`, in
the worked example) fires.

An editor or agent authoring this action should look the capability up first,
`GET /wayfinder/service-blueprint-authoring/support-systems` (REST) or `list_support_systems`
(MCP) list every registered support system exactly like `component-types`/`list_component_types`
do for the component catalog, to get the real `supportSystemKey`/`capabilityKey`, which inputs
are required, and which outcome keys the calling stage's outgoing routes must match. The visual
editor's own stage action editor drives this same lookup live, see
[docs/skills/canvas-editor/SKILL.md](../skills/canvas-editor/SKILL.md).

## Making a support-system call *mandatory*

A caseworker who can simply choose not to consult the insurer isn't much of a control. The
juggling-licence blueprint makes it compulsory, and does so declaratively, the review stage's own
routes decide which single action is even *offered*, using
[route visibility](./calculation-language.md#route-visibility-showwhen-on-a-route)
(`ServiceBlueprintRouteDefinition.ShowWhen`):

```json
{
  "id": "under-review--send-to-insurer--to-insurer-check",
  "target": "to-insurer-check",
  "trigger": "send-to-insurer",
  "label": "Send risk assessment to insurer",
  "showWhen": "riskAssessment <> ''"
},
{
  "id": "under-review--continue--to-caseworker-decision",
  "target": "to-caseworker-decision",
  "trigger": "continue",
  "label": "Continue to decision",
  "showWhen": "riskAssessment = ''"
}
```

A caseworker reviewing an application with a risk assessment attached sees exactly one button,
"Send risk assessment to insurer", "Continue to decision" isn't rendered, isn't in
`AvailableActions`, and submitting its trigger anyway is rejected the same as any other action
that was never declared. Reviewing and deciding stay **separate caseworker stages**
(`under-review` → `caseworker-decision`) so there's a real point in the journey where "have we
consulted the insurer?" gates what happens next, rather than three equal buttons on one screen.

Note what is *not* here: no host code, no bespoke C#. It's worth being precise about what this
mechanism *isn't*, too, it isn't conditional routing in the graph-theoretic sense. Wayfinder's
Split gateways still deliberately fan out to every route rather than choosing one; `ShowWhen` only
ever changes which routes a *human* is offered on a stage, evaluated the same way and with the
same fail-open bias as a component's own `showWhen`.

**This replaced an earlier, worse version of the same idea.** The first working version of this
requirement used a `StageDefinition.Validations` rule scoped to a single action
(`ServiceBlueprintStageValidationRule.Actions`), both buttons always shown, "Continue to
decision" blocked with an error message if clicked with a file attached. That's a legitimate
pattern (see the "Route `showWhen` vs. a scoped stage validation rule" callout in the
[calculation-language guide](./calculation-language.md#route-visibility-showwhen-on-a-route)),
but it was the wrong tool for *this* stage, where the two exits are genuinely different courses of
action, not one action with an extra data requirement. It also depended on an editor route-condition
UI (an always/event/guard mode selector) that had existed in the codebase since before this
feature, looked functional, but was never evaluated by the engine anywhere and, because of a
client/server wire-key mismatch, didn't even survive a save. `ShowWhen` on the route itself is
what that UI should have been from the start: authored with the same
`wayfinder-calculation-expression-editor` intellisense as a stage validation's `when`/`rule`.

## The worked example, verified end to end

`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`'s `under-review` stage (now
"Review application") offers exactly one of two routes, gated by `ShowWhen` as shown above:
`send-to-insurer`, targeting a `to-insurer-check` Split gateway that forks the caseworker's own
cursor (straight to `insurer-check-complete`, a Join) from a new `automation`-queue cursor
(`insurer-validation`, carrying the `support-system-call` action); or `continue`, when there's
nothing to send, straight to `caseworker-decision`. Once SafetyNet Underwriting resolves,
`insurer-check-complete` releases into `caseworker-decision`, a separate stage from `under-review`,
so the insurer's decision is already on screen by the time the caseworker makes the actual final
call, flowing into the pre-existing, unmodified `post-review` join every application already went
through.

Run `dotnet run --project Wayfinder.AppHost`, sign in as `caseworker@example.test` /
`applicant@example.test` (password `wayfinder-demo`), and the whole path is real: a citizen's
uploaded risk-assessment file genuinely travels server-to-server to SafetyNet Underwriting's own
running app, its staff queue at `/queue` (a separate `Wayfinder.AppHost` resource, a distinct
"Staff queue" dashboard link) shows the real submission, and approving it there resolves the
caseworker's own wait screen via a real webhook call back into
`POST /wayfinder/support-systems/callbacks/{invocationId}`.

This exact path is also a real, automated Playwright spec,
`Wayfinder.ReferenceApp.Tests/tests/support-systems-live.spec.ts`, run with
`npm run test:playwright:live` (not the default `npm run test:playwright`, which boots
`Wayfinder.ReferenceApp` directly and has no Aspire service discovery to resolve
`http://safetynet-underwriting` with). `tests/support/live-app-host.ts` boots the real
`Wayfinder.AppHost` stack and polls both resources' own HTTP endpoints until they're genuinely
answering before any test runs, precedent: Umbraco.Prism's own
`UmbracoPrism.Client/tests/support/live-app-host.ts`, proportionately leaner here (two plain
in-memory apps, nothing to seed). The spec drives three separate browser contexts, applicant,
caseworker, and SafetyNet Underwriting's own staff, through the real UI on both real apps, not
an API shortcut standing in for either.

## Two things the first working version got wrong

Both found by recording the journey end to end and watching it, not by any test suite, worth
naming because they generalise beyond this feature.

**A backstage actor waiting on a support system must stay on their own worklist.** The engine's
queue worklist originally meant "what can I act on", so it filtered to items with available
actions. A caseworker's cursor parked at a join gateway has none, so the moment an application
was sent to the insurer it vanished from the caseworker's queue entirely, reachable only by a
remembered URL. `QueueWorkItem.IsWaiting` now carries "in your queue, nothing to do yet", the
worklist includes those items, and the reference app renders them with a "Waiting" tag and a
"View" link. This was never support-systems-specific: *any* actor parked at *any* join gateway
was invisible to their own queue. It only surfaced now because before this feature, the only
actor who ever waited at a join was the citizen, who has always had a dedicated wait screen.

**Viewing an uploaded file is not a new component.** The engine deliberately never sees file
bytes or URLs, it holds an opaque `ServiceRequestFileReference`, and `IServiceRequestFileStorage`
leaves both storage *and* routing to the host. So a blueprint-authored "file download" component
would have to make the engine mint URLs it has no business knowing. Instead the host fills in
`FieldRenderPayload.FileUrl` on the way to the renderer, and a `file-upload` field's read-only
summary row renders its filename as a real link (see `WithFileDownloadUrls` in
`Wayfinder.ReferenceApp/Program.cs`). A host with no download route leaves it null and gets plain
filename text, exactly as before. The insurer's own file view is not a Wayfinder concern at all,
SafetyNet Underwriting serves the bytes it received from its own `GET /submissions/{id}/file`.

## Related documentation

- [Reference Service Blueprint Contract](./reference-service-blueprint-contract.md), the full
  `ServiceBlueprint` JSON shape.
- [Extending the component catalog](./extending-the-component-catalog.md), the same
  descriptor-driven registration pattern this feature mirrors, including the full
  `ComponentPropertyDescriptor` reference and reference-aware `Format` tags.
- [The Wayfinder Reference App](./reference-app.md), the juggling-licence demo blueprint and
  SafetyNet Underwriting, the worked example this document refers to throughout.
