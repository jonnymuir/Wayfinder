# Building a service with Wayfinder

This guide is for whoever is building a site or a service with Wayfinder: a developer, or an AI
agent working for one. It answers the question that comes before any blueprint is written:
**which part of what I am building belongs in a blueprint, and which part belongs somewhere
else?** It is also published as an MCP resource (`service-blueprint-docs://building-with-wayfinder`),
so an agent connected to a Wayfinder host can read it without a repo checkout.

Wayfinder's promise is that a service is configuration, not code. The more of a service that
lives in its blueprint, the less code there is to build, test, secure and keep in step with the
design. A build that recreates forms, steps or decision rules in host code loses that, and the
blueprint editor, the design partner and the caseworker worklist can no longer see the service.

Host-specific guides build on this one:
[Building a site with Wayfinder for Umbraco](https://github.com/jonnymuir/Wayfinder.Umbraco/blob/main/docs/building-a-site.md).

## Where each part of a service lives

| What you are building | Where it lives | Not |
|---|---|---|
| A form, a question page, a check-your-answers page, a confirmation | A stage in a blueprint, with [components](./reference-service-blueprint-contract.md) | A page template, a view, a controller action |
| The order of steps, and which step comes next | Routes and gateways in the blueprint | Redirects or `if` statements in host code |
| Eligibility, branching and validation rules | Gateway conditions, component validation, `showWhen` | Validation attributes or JavaScript |
| A sum, an estimate, a running total, a lookup table | The blueprint's [`calculations`](./calculation-language.md) block | A host service, a client-side script |
| Work a team picks up and decides on | A stage in a backstage queue, shown on the worklist | A bespoke admin screen |
| Who can see and act on which queue | `roleGates`, `assignmentPolicy` and the host's actor profile | Checks scattered through views |
| Asking another system something, or telling it something | A [support system](./support-systems.md) call | An HTTP client called from a controller |
| Facts about the signed-in person the service starts from | [Service inputs](./calculation-language.md) (`source: service`) the host supplies | Values read from claims in a view |
| Look and feel | The host's design tokens over the GOV.UK Design System markup | Replacement markup or a parallel CSS framework |
| Editorial pages: home, about, guidance, news, marketing | The host's own content | A blueprint |

The last row matters as much as the others. A blueprint is for a transaction: something a person
or a team does, with state that moves on. A page that only informs is content, and stays content.

## Signs a build has gone the wrong way

Stop and move the work into the blueprint when you find yourself:

- creating a page template or content type per question or per step;
- writing a controller, form handler or API endpoint that receives a submission;
- storing answers in your own table, session or content node;
- writing branching, eligibility or arithmetic in C#, Razor or JavaScript;
- building a staff screen that lists cases or records a decision;
- copying GOV.UK markup into a template to restyle it, rather than changing tokens;
- calling a business system's API directly from a view or controller.

Each one has a home in the table above. When none of them fits, that is a real gap: say so,
rather than building around it.

## Turning an existing design into Wayfinder

When you are given a finished design, a prototype or an existing site to rebuild:

1. **Inventory it.** List every page and interaction, and give each one a kind from the table
   above: content, a stage in a journey, a staff task, a data lookup, a notification, styling.
2. **Find the journeys.** Group the stages into services. One service is one blueprint: the
   steps one person, and the teams behind them, go through to get one outcome. Name the queues
   (lanes) each blueprint needs.
3. **Find the data.** For every value a page shows that the person did not type, decide where it
   comes from: the signed-in person (a service input), or another system (a support system).
4. **Map the look.** Take the design's colours, type and spacing into the host's design tokens.
   Note anything the GOV.UK components cannot show; do not replace their markup to get it.
5. **Report the gaps before building.** Anything that fits no row above, or that the component
   catalog (`list_component_types`) cannot express, goes back to whoever owns the design as a
   decision, with the closest Wayfinder way of doing it beside it.
6. **Build blueprint first.** Author each blueprint, validate and simulate it, then put it on a
   page. Content and styling come last, and are small.

## Data from business systems

Most services need data held somewhere else: a member record, a scheme, an existing case. Two
mechanisms cover it, and neither puts that data's logic in a view.

- **Service inputs** are values the host supplies when the service starts, through
  `ResolveServiceInputs`: the signed-in person's name, their member number, their scheme. Use
  them for facts the journey needs from its first step, and for calculations.
- **Support system calls** are the blueprint asking a system something, or telling it
  something, part way through: look up a member, check eligibility, open a case, record a
  decision. The call is a stage in a system lane (an automation queue), and the answer chooses
  the route out of it. Its outputs are written into the request, where a `summary-list` or a
  calculation can read them.

### Start with a mock

You rarely have the real business system on day one. Register it as a support system with
`mock: true`, and give each capability the answer it should give. The journey runs end to end
with no external API, and when the real system is ready, you replace the `mock` entry with an
`endpoint`. The blueprint does not change. See
[Mocking a support system](./support-systems.md#mocking-a-support-system).

Write down, beside the mock, what each capability assumes the real system will accept and
return. That list becomes the contract the real integration is built to.

### Looking up a record

To show a member's details, or anything else held elsewhere, partway through a journey:

1. Declare a capability, for example `get-member`, with the inputs it needs (`memberNumber`), the
   outputs it returns (`memberName`, `schemeName`) and its outcomes (`found`, `not-found`).
2. In the blueprint, route into a split gateway that sends the person's cursor to a join and an
   automation-queue cursor to a stage whose `support-system-call` action calls `get-member`.
3. Route the join's outcomes to the stages that follow: a `summary-list` bound to `memberName`
   and `schemeName` when the member is found, a helpful dead end when not.

The person sees the join's waiting page while the call is in flight. A mock answers on the
engine's first check, so with a mock the wait is momentary.

## What still needs host code

A Wayfinder host still writes a small amount of code, and should write no more than this:

- **Identity and access:** who the signed-in person is, and which queues they can see and act
  on (the actor profile).
- **Service inputs:** the `ResolveServiceInputs` that supplies facts about the signed-in person.
- **File storage:** where uploaded files are kept, if a service takes uploads.
- **A real support system client**, only when a system cannot be reached through the
  configuration-only webhook contract.
- **Styling:** design tokens, and very rarely a component override.

Everything else is configuration: blueprints, support system entries, and the host's content.

## Related documentation

- [Reference Service Blueprint Contract](./reference-service-blueprint-contract.md), the full
  blueprint shape.
- [Service design principles](./service-design-principles.md), for judging a blueprint once it is
  written.
- [Support systems](./support-systems.md), registering, mocking and calling another system.
- [Calculation language](./calculation-language.md), including service inputs.
