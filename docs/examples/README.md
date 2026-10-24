# Example service blueprints

Complete blueprints the guides teach from, in roughly increasing order of complexity. Every one
passes the same validation an authoring save applies (`DocumentedExampleBlueprintTests`), so a
model change that breaks an example fails CI rather than leaving a guide describing a blueprint
that no longer loads.

| Blueprint | Shows |
|---|---|
| [`planning-notification.yaml`](./planning-notification.yaml) | A planning application flow. |
| [`community-enquiry.yaml`](./community-enquiry.yaml) | Two queues, applicant and reviewer, with an approval loop. |
| [`information-request.yaml`](./information-request.yaml) | Two queues, an SLA-driven review. |
| [`payment-demo.yaml`](./payment-demo.yaml) | Split **and** Join gateways in a payment flow. |
| [`money-modeller.yaml`](./money-modeller.yaml) | A full `calculations` block with lookup tables, live sliders, stat groups, a chart and a recalculate self-loop. Walked through in [the calculation language guide](../guides/calculation-language.md#worked-example-money-modellerjson). |

The reference app's own blueprints, in
[`Wayfinder.ReferenceApp/service-blueprints/`](../../Wayfinder.ReferenceApp/service-blueprints/),
are the runnable ones: see [the reference app guide](../guides/reference-app.md).
