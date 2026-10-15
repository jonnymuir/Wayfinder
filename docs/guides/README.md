# Wayfinder guides

Every guide, grouped by what you're trying to do. New to Wayfinder? Read the
[README quickstart](../../README.md#quickstart), then the
[reference app guide](./reference-app.md), then come back here. The
[glossary](../glossary.md) defines the terms all of these guides use.

## Design and author a service blueprint

For whoever writes blueprints, by hand, in the visual editor, or with an AI agent.

- **[Service design principles](./service-design-principles.md)**: the NN/g service blueprint
  model, the Double Diamond, the GOV.UK Service Standard and Lou Downe's principles of good
  services, each mapped to a concrete authoring decision. Read this before drafting.
- **[Reference service blueprint contract](./reference-service-blueprint-contract.md)**: the
  full `ServiceBlueprint` JSON shape: queues, stages, gateways, routes, components and response
  states.
- **[The calculation language](./calculation-language.md)**: the grammar and functions for the
  `calculations` block and `showWhen`, with a worked example.
- **[Editor skill docs](../skills/README.md)**: a screenshot tour of each tab in the visual
  editor.
- **[Example blueprints](../examples/)**: five validated blueprints, from a linear enquiry to a
  full pension modeller.

## Host the engine in your own app

For whoever builds the application a blueprint runs in.

- **[The reference app](./reference-app.md)**: a small, complete host in this repo, and how
  little of it is Wayfinder's problem.
- **[AI-ready authoring](./ai-service-blueprint-authoring.md)**: expose list, read, validate,
  simulate and save over REST and MCP, and connect Claude Code or any MCP client.
- **[Extending the component catalog](./extending-the-component-catalog.md)**: register a new
  component type.
- **[Request concurrency](./request-concurrency.md)**: control "is there already one?" beyond a
  blueprint's `requestPolicy`.

## Run caseworker queues

For hosts with backstage work: who can see a queue, who picks an item up, and how a worklist
behaves.

- **[Work allocation](./work-allocation.md)**: queue eligibility, pickup and ownership, "pick
  up next available", and the audit trail. Read this first.
- **[Team assignment](./team-assignment.md)**: team trays and initiator ownership on top of
  work allocation.
- **[Queue worklist filtering](./queue-worklist-filtering.md)**: status filters, sorting, search
  and paging for a caseworker worklist.

## Connect other systems

- **[Support systems](./support-systems.md)**: model NN/g's support-processes lane: call an
  external system from a stage and wait for its outcome, from configuration alone or with a
  custom client.
- **[Bulk data review](./bulk-data-review.md)**: a paginated "only show me what needs
  attention" review over a whole-file CSV exchange.

## See it working

- **[Demo recordings](../demos/README.md)**: narrated single-take recordings of features working
  end to end, each claim backed by a test assertion.
