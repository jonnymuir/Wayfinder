# Support systems

How Wayfinder models Nielsen Norman Group's third service-blueprint lane — the
external/downstream systems a backstage actor calls out to — and how a toolkit integrator
registers one. This is for whoever is *building* a Wayfinder host, not whoever is *authoring* a
service blueprint against one; if that's you, see
[Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) instead.

This document is also exposed as an MCP resource (`service-blueprint-docs://support-systems`)
so an agent can fetch it directly without repo access.

---

## Where this sits in the service-blueprint model

NN/g's article (nngroup.com/articles/service-blueprints-definition) defines **support
processes** as "internal steps, and interactions that support the employees in delivering the
service" — credit-card verification, pricing, quality testing are its own examples. Wayfinder's
reference app already models two of the five NN/g layers as queues: a citizen-facing
(frontstage) queue and a caseworker-facing (backstage) queue. A **support system** is the third:
an external, API-driven actor a caseworker's own stage calls out to, and potentially waits on,
before finishing their own decision.

The worked example in this repo (`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`):
a caseworker reviewing a juggling-event licence application sends the applicant's uploaded risk
assessment to **SafetyNet Underwriting**, a fictional insurer, and waits for its approve/reject
decision before finishing their own review.

## The abstraction: capability-declared completion mode

A support system doesn't get bespoke engine code. It's a **descriptor** — what the system is
called and what it can do — plus a small client the host registers alongside it. Two things stay
deliberately generic rather than baked into any one integration:

- **Inputs** a capability needs are declared using `ComponentPropertyDescriptor` — the exact
  same recursive shape already shared with action parameters and component properties (see
  [Extending the component catalog](./extending-the-component-catalog.md)). An input tagged
  `Format: "field-ref"` gets the existing reference-aware field-ref editor machinery for free —
  no bespoke input-authoring UI. A capability can declare more than one input.
- **How the outcome comes back** — poll or webhook — is declared per capability, not assumed by
  the engine. The engine always offers *both* mechanisms as generic, always-on plumbing: a
  poll-check hook (invoked whenever a client re-polls a waiting stage, reusing the exact
  defer/poll envelope a join gateway already returns) and a generic webhook receiver (resolving
  an opaque invocation id back to the pending cursor). A capability's
  `SupportedCompletionModes` tells the engine which of those two it should actually use for that
  capability — the engine only calls the client's status-check method if `Poll` is declared, and
  only hands the client a callback URL if `Webhook` is declared. A capability can declare both,
  as SafetyNet Underwriting's does, to demonstrate both paths genuinely resolving the same call.

This means adding a new support system with a different completion strategy — synchronous-only,
webhook-only, poll-only — needs no engine change, only a new descriptor and a new client.

## `SupportSystemDescriptor` reference

| Field | Meaning |
|---|---|
| `Key` | Unique across the whole process — a duplicate throws at registration time. |
| `DisplayName` | Human-readable name, e.g. "SafetyNet Underwriting". |
| `Description` | Longer help text — editor tooltip / AI-agent-readable prose. |
| `Capabilities` | `IReadOnlyList<SupportSystemCapabilityDescriptor>` — see below. Must be registered with at least the capabilities a blueprint will reference. |

## `SupportSystemCapabilityDescriptor` reference

| Field | Meaning |
|---|---|
| `Key` | Unique within the support system, e.g. `"validate-risk-assessment"`. |
| `DisplayName` | Human-readable name for editor UI. |
| `Inputs` | `IReadOnlyList<ComponentPropertyDescriptor>` — what this capability needs, reusing the exact same property-descriptor shape as a component's own properties. |
| `Outputs` | `IReadOnlyList<ComponentPropertyDescriptor>` — blueprint field keys this capability's resolution writes directly into instance state once it resolves (e.g. a decision note). A `summary-list`/`stat-group` elsewhere in the blueprint may legitimately bind to one of these even though no stage ever captures it as an input — see [Validation](#validation) below. Unlike `Inputs`, there's no per-blueprint remapping: the `Key` declared here is exactly the field key a host's `ISupportSystemClient` is expected to write. |
| `SupportedCompletionModes` | `Poll` and/or `Webhook` — must declare at least one, or an invocation could never resolve. |
| `Outcomes` | The closed set of decisions this capability can resolve to, e.g. `approved`/`rejected` — must declare at least one. A blueprint's outgoing routes from the calling stage are validated against this vocabulary. |

## Validation

Registering a descriptor gets a blueprint referencing it real, comprehensive validation for
free — `ServiceBlueprint.ValidateSupportSystemActions()` (surfaced through
`validate_service_blueprint`/`save_service_blueprint`, the same as
[component property validation](./extending-the-component-catalog.md#validation-comes-for-free)):

- `supportSystemKey`/`capabilityKey` are present and actually registered
  (`SUPPORT_SYSTEM_ACTION_MISSING_KEYS`/`_UNKNOWN_SUPPORT_SYSTEM`/`_UNKNOWN_CAPABILITY`).
- Every input the capability declares `Required` is bound in the action's own `params.inputs`
  (`SUPPORT_SYSTEM_ACTION_MISSING_REQUIRED_INPUT`), and every key in `params.inputs` actually
  names a declared input (`SUPPORT_SYSTEM_ACTION_UNKNOWN_INPUT` — catches a typo).
- Each bound input's blueprint field key actually exists somewhere in the blueprint
  (`SUPPORT_SYSTEM_ACTION_INPUT_UNKNOWN_FIELD`).
- The carrying stage's own outgoing route triggers are all outcomes the capability can actually
  resolve to (`SUPPORT_SYSTEM_ACTION_ROUTE_TRIGGER_UNKNOWN_OUTCOME`) — a route whose trigger isn't
  a declared outcome can never fire, since `ResolveSupportSystemOutcome` only ever delivers one.

Separately, `ValidateDataDisplayBindings()` treats every field key declared in a referenced
capability's `Outputs` as a known, legitimate binding for a `summary-list`/`stat-group` anywhere
in the blueprint — the same as a captured input field or a `calculations.fields` entry — so
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
                new() { Key = "File", Title = "Risk assessment file", ValueKind = ComponentPropertyValueKind.String, Format = "field-ref", Required = true },
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
run** — `SupportSystemRegistry` freezes the first time anything actually reads it (`.All`,
`.Find`, `.FindCapability`), and `Register` throws after that, loudly, the same registration
discipline `ComponentTypeRegistry` already enforces (see
[Extending the component catalog § Registration timing](./extending-the-component-catalog.md#registration-timing-the-registry-freezes)).

Registering the descriptor alone gets you validation and editor authoring support for
referencing the capability. Actually calling out to the real external system is a separate
`ISupportSystemClient` implementation the host also registers alongside it — see
`Wayfinder.Engine/Abstractions/ISupportSystemClient.cs`. `ProcessManagerEngine`'s constructor
takes an `IEnumerable<ISupportSystemClient>`, keyed internally by
`ISupportSystemClient.SupportSystemKey`.

## Delivering the outcome

`ProcessManagerEngine.ResolveSupportSystemOutcome(invocationId, outcomeKey, resultPayload?)` is
the one method that actually advances a waiting automation-queue cursor once a capability's
outcome is known — the single code path both delivery mechanisms end up calling:

- **Poll**: entirely automatic. Every time a client re-polls a waiting join gateway (the normal
  `GetCurrent` a caseworker's own browser already does behind the scenes), the engine checks any
  still-pending invocation blocking that gateway whose capability declared `Poll` support, calls
  the client's `CheckStatusAsync`, and calls `ResolveSupportSystemOutcome` itself if it got an
  answer. Nothing for a host to wire up.
- **Webhook**: a host's own job, the same way `GetCurrent`/`Advance` themselves are — this
  toolkit's authoring surface (`Wayfinder.Engine.Api`) is deliberately scoped to blueprint
  *authoring* only, not runtime request handling, so it doesn't ship a webhook route itself. Add
  one directly against your `ProcessManagerEngine` instance, e.g.:

  ```csharp
  // CallbackPayload is a small host-defined DTO { string OutcomeKey, JsonObject? ResultPayload }
  // matching whatever shape the external system's callback actually posts — Wayfinder doesn't
  // prescribe one.
  app.MapPost("/wayfinder/support-systems/callbacks/{invocationId}", (
      string invocationId, CallbackPayload payload, ProcessManagerEngine engine) =>
      Results.Ok(engine.ResolveSupportSystemOutcome(invocationId, payload.OutcomeKey, payload.ResultPayload)));
  ```

  `invocationId` is an unguessable per-invocation token generated when the capability's onEnter
  action ran (`SupportSystemInvocationContext.InvocationId`, handed to the client's `InvokeAsync`)
  — treat it as the correlation/auth token an external system's callback proves it, the same
  reasoning `Wayfinder.ReferenceApp`'s already-minimal auth boundary applies elsewhere.

A capability declaring both modes (SafetyNet Underwriting's does) may have both resolve the same
invocation — `ResolveSupportSystemOutcome` marks an invocation resolved before advancing anything,
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
    "inputs": { "File": "riskAssessment" }
  }
}
```

The stage carrying this action then sits waiting — using the same `waitingContent`/
`waitingPollIntervalMs`/`requiredIncomingQueues` fields a join gateway already uses for the
citizen's "waiting behind the line of visibility" screen — until the capability resolves to one
of its declared `Outcomes`, at which point the matching outgoing route (`approved`/`rejected`, in
the worked example) fires.

An editor or agent authoring this action should look the capability up first —
`GET /wayfinder/service-blueprint-authoring/support-systems` (REST) or `list_support_systems`
(MCP) list every registered support system exactly like `component-types`/`list_component_types`
do for the component catalog — to get the real `supportSystemKey`/`capabilityKey`, which inputs
are required, and which outcome keys the calling stage's outgoing routes must match. The visual
editor's own stage action editor drives this same lookup live — see
[docs/skills/canvas-editor/SKILL.md](../skills/canvas-editor/SKILL.md).

## The worked example, verified end to end

`Wayfinder.ReferenceApp/service-blueprints/juggling-licence.json`'s `under-review` stage gets a
third route, `send-to-insurer`, alongside its existing `approve`/`reject` — additive, not a
replacement — targeting a `to-insurer-check` Split gateway that forks the caseworker's own cursor
(straight to `insurer-check-complete`, a Join) from a new `automation`-queue cursor
(`insurer-validation`, carrying the `support-system-call` action). Once SafetyNet Underwriting
resolves, `insurer-check-complete` releases back into `under-review` itself, now showing the
insurer's decision, where the caseworker makes the actual final call — flowing into the
pre-existing, unmodified `post-review` join every application already went through.

Run `dotnet run --project Wayfinder.AppHost`, sign in as `caseworker@example.test` /
`applicant@example.test` (password `wayfinder-demo`), and the whole path is real: a citizen's
uploaded risk-assessment file genuinely travels server-to-server to SafetyNet Underwriting's own
running app, its staff queue at `/queue` (a separate `Wayfinder.AppHost` resource — a distinct
"Staff queue" dashboard link) shows the real submission, and approving it there resolves the
caseworker's own wait screen via a real webhook call back into
`POST /wayfinder/support-systems/callbacks/{invocationId}`.

## Related documentation

- [Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) — the full
  `ServiceBlueprint` JSON shape.
- [Extending the component catalog](./extending-the-component-catalog.md) — the same
  descriptor-driven registration pattern this feature mirrors, including the full
  `ComponentPropertyDescriptor` reference and reference-aware `Format` tags.
- [The Wayfinder Reference App](./reference-app.md) — the juggling-licence demo blueprint and
  SafetyNet Underwriting, the worked example this document refers to throughout.
