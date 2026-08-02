using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// "Apply for a licence to hold a juggling event" — the reference host's seed service
/// blueprint, built entirely in code (no JSON, no filesystem — genuinely in-memory). It's
/// GOV.UK Service Manual's own long-running teaching exemplar for service design, chosen
/// deliberately: a small, low-stakes fictional service everyone already recognises, rather
/// than something bespoke this repo would need to explain from scratch.
/// <para>
/// Two queues, matching NN/g's frontstage/backstage split
/// (https://www.nngroup.com/articles/service-blueprints-definition/): <see cref="ReferenceActors.CitizenQueue"/>
/// is the applicant's own journey, <see cref="ReferenceActors.CaseworkerQueue"/> is the
/// review team's worklist behind the line of visibility.
/// </para>
/// <para>
/// Every stage route targets a gateway, never another stage directly — the rule
/// <c>ServiceBlueprint.ValidateGatewayRouting()</c> enforces (see
/// docs/guides/reference-service-blueprint-contract.md's "gateway routing rule"). Even a
/// single-route handoff gets its own trivial pass-through gateway (a single "continue"
/// route), and a stage with two mutually-exclusive routes — like <c>under-review</c>'s
/// approve/reject — gets one dedicated gateway per outcome, exactly the shape
/// <c>money-modeller.json</c>'s <c>choose-start</c> stage already uses for its own two-route
/// branch. Nothing here uses Split's actual fan-out behaviour (multiple routes firing off one
/// shared trigger) or Join's wait-for-multiple-cursors behaviour — every gateway below is a
/// single-route pass-through, so <c>GatewayType</c> is "Split" purely by the same convention
/// the existing fixtures use for that trivial case, not because anything here fans out.
/// </para>
/// </summary>
public static class JugglingLicenceBlueprint
{
    public const string DefinitionKey = "juggling-licence";

    private static readonly SummaryListComponent ApplicationSummary = new()
    {
        Title = "Application details",
        Children =
        [
            new TextInputComponent { FieldKey = "applicantName", Label = "Full name" },
            new EmailComponent { FieldKey = "applicantEmail", Label = "Email address" },
            new TextInputComponent { FieldKey = "eventName", Label = "Name of the event" },
            new DateInputComponent { FieldKey = "eventDate", Label = "Date of the event" },
            new NumberInputComponent { FieldKey = "jugglerCount", Label = "Number of jugglers taking part" }
        ]
    };

    public static ServiceBlueprint Build() => new()
    {
        DefinitionKey = DefinitionKey,
        DisplayName = "Apply for a licence to hold a juggling event",
        Description =
            "GOV.UK Service Manual's own teaching exemplar, used here to demonstrate a " +
            "two-actor service blueprint: an applicant's frontstage journey and a " +
            "caseworker's backstage review queue.",
        Version = 1,
        InitialStage = "your-details",
        RequestPolicy = "single",
        Queues =
        [
            new QueueDefinition { Key = ReferenceActors.CitizenQueue, DisplayName = "Applicant", Actor = "citizen" },
            new QueueDefinition { Key = ReferenceActors.CaseworkerQueue, DisplayName = "Caseworker", Actor = "caseworker" }
        ],
        Stages =
        [
            new StageDefinition
            {
                StageKey = "your-details",
                DisplayName = "Your details",
                QueueKey = ReferenceActors.CitizenQueue,
                Components =
                [
                    new FieldsetComponent
                    {
                        Legend = "Your details",
                        Children =
                        [
                            new TextInputComponent { FieldKey = "applicantName", Label = "Full name", Required = true },
                            new EmailComponent { FieldKey = "applicantEmail", Label = "Email address", Required = true }
                        ]
                    }
                ],
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "your-details--continue--to-event-details",
                        Target = "to-event-details",
                        Trigger = "continue",
                        Label = "Continue",
                        Style = "primary"
                    }
                ]
            },
            new StageDefinition
            {
                StageKey = "event-details",
                DisplayName = "About the event",
                QueueKey = ReferenceActors.CitizenQueue,
                Components =
                [
                    new FieldsetComponent
                    {
                        Legend = "About the event",
                        Children =
                        [
                            new TextInputComponent { FieldKey = "eventName", Label = "Name of the event", Required = true },
                            new DateInputComponent { FieldKey = "eventDate", Label = "Date of the event", Required = true },
                            new NumberInputComponent
                            {
                                FieldKey = "jugglerCount",
                                Label = "Number of jugglers taking part",
                                Required = true,
                                Min = 1
                            }
                        ]
                    }
                ],
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "event-details--continue--to-declaration",
                        Target = "to-declaration",
                        Trigger = "continue",
                        Label = "Continue",
                        Style = "primary"
                    }
                ]
            },
            new StageDefinition
            {
                StageKey = "declaration",
                DisplayName = "Check your answers and declare",
                QueueKey = ReferenceActors.CitizenQueue,
                Components =
                [
                    ApplicationSummary,
                    new BooleanComponent
                    {
                        FieldKey = "declarationConfirmed",
                        Label = "I confirm the details above are correct",
                        Required = true
                    }
                ],
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "declaration--submit--to-under-review",
                        Target = "to-under-review",
                        Trigger = "submit",
                        Label = "Submit application",
                        Style = "primary"
                    }
                ]
            },
            new StageDefinition
            {
                StageKey = "under-review",
                DisplayName = "Application under review",
                QueueKey = ReferenceActors.CaseworkerQueue,
                Components =
                [
                    new PanelComponent { Heading = "Application under review" },
                    new BodyComponent
                    {
                        Content = "A caseworker will review this application and record a decision below."
                    },
                    ApplicationSummary
                ],
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "under-review--approve--to-approved",
                        Target = "to-approved",
                        Trigger = "approve",
                        Label = "Approve",
                        Style = "primary"
                    },
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "under-review--reject--to-rejected",
                        Target = "to-rejected",
                        Trigger = "reject",
                        Label = "Reject",
                        Style = "destructive"
                    }
                ]
            },
            new StageDefinition
            {
                StageKey = "approved",
                DisplayName = "Licence granted",
                QueueKey = ReferenceActors.CitizenQueue,
                Components =
                [
                    new PanelComponent { Heading = "Licence granted" },
                    new BodyComponent
                    {
                        Content = "Your licence to hold a juggling event has been granted. Keep this confirmation for your records."
                    }
                ]
            },
            new StageDefinition
            {
                StageKey = "rejected",
                DisplayName = "Application not approved",
                QueueKey = ReferenceActors.CitizenQueue,
                Components =
                [
                    new PanelComponent { Heading = "Application not approved" },
                    new BodyComponent
                    {
                        Content = "Your application was not approved this time. Contact the licensing team for more information."
                    }
                ]
            }
        ],
        Gateways =
        [
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-event-details",
                DisplayName = "Continue to event details",
                GatewayType = "Split",
                QueueKey = ReferenceActors.CitizenQueue,
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "to-event-details--continue--event-details",
                        Target = "event-details",
                        Trigger = "continue"
                    }
                ]
            },
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-declaration",
                DisplayName = "Continue to declaration",
                GatewayType = "Split",
                QueueKey = ReferenceActors.CitizenQueue,
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "to-declaration--continue--declaration",
                        Target = "declaration",
                        Trigger = "continue"
                    }
                ]
            },
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-under-review",
                DisplayName = "Hand off to caseworker",
                GatewayType = "Split",
                QueueKey = ReferenceActors.CitizenQueue,
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "to-under-review--continue--under-review",
                        Target = "under-review",
                        Trigger = "continue"
                    }
                ]
            },
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-approved",
                DisplayName = "Application approved",
                GatewayType = "Split",
                QueueKey = ReferenceActors.CaseworkerQueue,
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "to-approved--continue--approved",
                        Target = "approved",
                        Trigger = "continue"
                    }
                ]
            },
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-rejected",
                DisplayName = "Application rejected",
                GatewayType = "Split",
                QueueKey = ReferenceActors.CaseworkerQueue,
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "to-rejected--continue--rejected",
                        Target = "rejected",
                        Trigger = "continue"
                    }
                ]
            }
        ]
    };
}
