using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// SECURITY REGRESSION (availability: data loss). <see cref="ServiceRequest.HandedOverAt"/> is what
/// tells a store a request has left the person who started it, so it must not expire with their
/// session. Without it an anonymous visitor's submitted request is deleted when their session window
/// passes, even while it waits in a team's worklist. Observed on the persisted request, through a real
/// in-memory store, the way a host store sees it.
/// </summary>
public class RequestHandoverTests
{
    private const string TenantId = "tenant";
    private const string Visitor = "anonymous-visitor-1";
    private const string Caseworker = "caseworker-1";


    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"],
        StartableQueues = ["visitor"],
        ActionableQueues = ["visitor"],
        RestrictToInstanceOwner = true
    };

    private static readonly ActorProfile TeamProfile = new()
    {
        VisibleQueues = ["team"],
        StartableQueues = [],
        ActionableQueues = ["team"],
        RestrictToInstanceOwner = false
    };

    // A contact form: the visitor sends a message, the team replies, asks for more, or closes it.
    private const string ContactBlueprint = """
        definitionKey: contact
        displayName: Contact us
        version: 1
        initialStage: message
        requestPolicy: single
        queues:
          - key: visitor
            displayName: Visitor
            actor: citizen
          - key: team
            displayName: Team
            actor: caseworker
        stages:
          - stageKey: message
            displayName: Your message
            queueKey: visitor
            components:
              - type: textarea
                fieldKey: message
                label: Message
            routes:
              - id: message--submit--reply
                target: reply
                trigger: submit
          - stageKey: reply
            displayName: Reply
            queueKey: team
            components:
              - type: textarea
                fieldKey: answer
                label: Answer
            routes:
              - id: reply--ask--message
                target: message
                trigger: ask-for-more
              - id: reply--close--closed
                target: closed
                trigger: close
          - stageKey: closed
            displayName: Closed
            queueKey: team
            components:
              - type: panel
                heading: Closed
        """;

    // A journey the visitor completes on their own: nothing ever leaves their queue.
    private const string SelfServiceBlueprint = """
        definitionKey: self-service
        displayName: Check your eligibility
        version: 1
        initialStage: answers
        requestPolicy: single
        queues:
          - key: visitor
            displayName: Visitor
            actor: citizen
        stages:
          - stageKey: answers
            displayName: Your answers
            queueKey: visitor
            components:
              - type: text
                fieldKey: postcode
                label: Postcode
            routes:
              - id: answers--submit--result
                target: result
                trigger: submit
          - stageKey: result
            displayName: Result
            queueKey: visitor
            components:
              - type: panel
                heading: You are eligible
        """;

    // The visitor's submission splits: they see an acknowledgement while the team gets a copy.
    private const string SplitBlueprint = """
        definitionKey: split-contact
        displayName: Contact us (split)
        version: 1
        initialStage: message
        requestPolicy: single
        queues:
          - key: visitor
            displayName: Visitor
            actor: citizen
          - key: team
            displayName: Team
            actor: caseworker
        stages:
          - stageKey: message
            displayName: Your message
            queueKey: visitor
            components:
              - type: textarea
                fieldKey: message
                label: Message
            routes:
              - id: message--submit--split
                target: to-team
                trigger: submit
          - stageKey: acknowledged
            displayName: Thank you
            queueKey: visitor
            components:
              - type: panel
                heading: Thank you
          - stageKey: reply
            displayName: Reply
            queueKey: team
            components:
              - type: textarea
                fieldKey: answer
                label: Answer
        gateways:
          - key: to-team
            displayName: Send to the team
            gatewayType: Split
            queueKey: visitor
            routes:
              - id: to-team--submit--acknowledged
                target: acknowledged
                trigger: submit
              - id: to-team--submit--reply
                target: reply
                trigger: submit
        """;

    private static (ProcessManagerEngine Engine, InMemoryServiceRequestStore Store) Build(string blueprintJson)
    {
        var definition = ServiceBlueprintYaml.Deserialize(blueprintJson);
        var store = new InMemoryServiceRequestStore();
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            instanceStore: store);
        return (engine, store);
    }

    private static ServiceRequest Stored(InMemoryServiceRequestStore store, string instanceId)
    {
        store.TryGet(instanceId, out var instance).Should().BeTrue();
        return instance;
    }

    private static ServiceRequestResponseEnvelope Submit(ProcessManagerEngine engine, ServiceRequestResponseEnvelope visitorView) =>
        engine.Advance(visitorView.InstanceId, TenantId, Visitor, VisitorProfile, "submit", visitorView.StateVersion, null);

    private static ServiceRequestResponseEnvelope TeamActs(ProcessManagerEngine engine, string instanceId, string trigger)
    {
        var item = engine.GetQueueWorkItems(TenantId, Caseworker, TeamProfile).Items.Single(i => i.InstanceId == instanceId);
        var pickedUp = engine.PickupWorkItem(instanceId, item.CursorId, TenantId, Caseworker, TeamProfile);
        return engine.Advance(instanceId, TenantId, Caseworker, TeamProfile, trigger, pickedUp.StateVersion, null);
    }

    [Fact]
    public void A_request_the_visitor_is_still_filling_in_has_not_been_handed_over()
    {
        var (engine, store) = Build(ContactBlueprint);

        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);

        Stored(store, started.InstanceId).HandedOverAt.Should().BeNull();
    }

    [Fact]
    public void Submitting_into_the_teams_queue_hands_the_request_over()
    {
        var (engine, store) = Build(ContactBlueprint);
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        var before = DateTimeOffset.UtcNow;

        Submit(engine, started);

        Stored(store, started.InstanceId).HandedOverAt.Should().NotBeNull()
            .And.BeOnOrAfter(before);
    }

    [Fact]
    public void A_request_sent_back_to_the_visitor_stays_handed_over()
    {
        var (engine, store) = Build(ContactBlueprint);
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        Submit(engine, started);
        var handedOverAt = Stored(store, started.InstanceId).HandedOverAt;
        handedOverAt.Should().NotBeNull();

        // The team's own answer is moved_on: the request is now in a queue it can't see (the same
        // releasing-call shape TeamAssignmentTests relies on). The move itself landed.
        TeamActs(engine, started.InstanceId, "ask-for-more");

        var stored = Stored(store, started.InstanceId);
        stored.CurrentStage.Should().Be("message", "the request is back in the visitor's own queue");
        stored.HandedOverAt.Should().Be(handedOverAt, "someone else has already taken it on; it must never expire with the visitor's session again");
    }

    [Fact]
    public void A_request_the_team_has_finished_stays_handed_over()
    {
        var (engine, store) = Build(ContactBlueprint);
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        Submit(engine, started);

        TeamActs(engine, started.InstanceId, "close");

        var stored = Stored(store, started.InstanceId);
        stored.CurrentStage.Should().Be("closed");
        stored.HandedOverAt.Should().NotBeNull();
    }

    [Fact]
    public void A_journey_the_visitor_completes_on_their_own_is_never_handed_over()
    {
        var (engine, store) = Build(SelfServiceBlueprint);
        var started = engine.GetCurrent("self-service", TenantId, Visitor, VisitorProfile);

        engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "submit", started.StateVersion, null);

        var stored = Stored(store, started.InstanceId);
        stored.CurrentStage.Should().Be("result");
        stored.HandedOverAt.Should().BeNull("an abandoned or finished self-service journey may still expire with the visitor's session");
    }

    [Fact]
    public void A_split_that_sends_part_of_the_request_to_the_team_hands_it_over()
    {
        var (engine, store) = Build(SplitBlueprint);
        var started = engine.GetCurrent("split-contact", TenantId, Visitor, VisitorProfile);

        Submit(engine, started);

        var stored = Stored(store, started.InstanceId);
        stored.Cursors.Select(c => c.QueueKey).Should().Contain("team", "the split left a position in the team's queue");
        stored.HandedOverAt.Should().NotBeNull();
    }
}
