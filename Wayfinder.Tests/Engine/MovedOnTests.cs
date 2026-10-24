using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// An advance that is saved but takes the request out of everything the actor can see answers
/// <c>moved_on</c>: their part is done, so it is neither an error nor a look at a stage that isn't theirs.
/// A move that was refused is still an error.
/// </summary>
public class MovedOnTests
{
    private const string TenantId = "tenant";
    private const string Visitor = "visitor-1";
    private const string Caseworker = "caseworker-1";


    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"], StartableQueues = ["visitor"], ActionableQueues = ["visitor"], RestrictToInstanceOwner = true
    };

    private static readonly ActorProfile TeamProfile = new()
    {
        VisibleQueues = ["team"], StartableQueues = [], ActionableQueues = ["team"], RestrictToInstanceOwner = false
    };

    private const string Blueprint = """
        definitionKey: contact
        displayName: Contact us
        version: 1
        initialStage: write
        requestPolicy: single
        queues:
          - key: visitor
            displayName: Visitor
            actor: citizen
          - key: team
            displayName: Team
            actor: caseworker
        stages:
          - stageKey: write
            displayName: Your message
            queueKey: visitor
            components:
              - type: conversation
                fieldKey: thread
                label: Your message
                required: true
            routes:
              - id: write--send--respond
                target: respond
                trigger: send
          - stageKey: respond
            displayName: Reply
            queueKey: team
            components:
              - type: conversation
                fieldKey: thread
                label: Reply
            routes:
              - id: respond--reply--write
                target: write
                trigger: reply
              - id: respond--close--closed
                target: closed
                trigger: close
          - stageKey: closed
            displayName: Closed
            queueKey: team
            components:
              - type: panel
                heading: Closed
        """;

    private static (ProcessManagerEngine Engine, InMemoryServiceRequestStore Store) Build()
    {
        var definition = ServiceBlueprintYaml.Deserialize(Blueprint);
        var store = new InMemoryServiceRequestStore();
        return (new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: store), store);
    }

    private static string SentToTheTeam(ProcessManagerEngine engine)
    {
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "send", started.StateVersion, new() { ["thread"] = "Hello" });
        return started.InstanceId;
    }

    private static ServiceRequestResponseEnvelope TeamActs(ProcessManagerEngine engine, string instanceId, string action, string fieldKey = "thread")
    {
        var item = engine.GetQueueWorkItems(TenantId, Caseworker, TeamProfile).Items.Single(i => i.InstanceId == instanceId);
        var pickedUp = engine.PickupWorkItem(instanceId, item.CursorId, TenantId, Caseworker, TeamProfile);
        return engine.Advance(instanceId, TenantId, Caseworker, TeamProfile, action, pickedUp.StateVersion, new() { [fieldKey] = "Hi" });
    }

    [Fact]
    public void ATeamReplyThatHandsTheRequestBack_IsAnsweredMovedOn_WithNoProblems()
    {
        var (engine, store) = Build();
        var instanceId = SentToTheTeam(engine);

        var answer = TeamActs(engine, instanceId, "reply");

        answer.ResponseState.Should().Be("moved_on");
        answer.Problems.Should().BeEmpty();
        answer.InstanceId.Should().Be(instanceId);
        store.TryGet(instanceId, out var saved).Should().BeTrue();
        saved.CurrentStage.Should().Be("write", "the reply was saved");
        answer.StateVersion.Should().Be(saved.StateVersion);
    }

    [Fact]
    public void MovedOn_ShowsTheActorNothingOfTheStageTheyCannotSee()
    {
        var (engine, _) = Build();
        var instanceId = SentToTheTeam(engine);

        TeamActs(engine, instanceId, "reply").Render.Should().BeNull();
    }

    [Fact]
    public void AnActorWhoCanStillSeeTheRequestAfterTheirMove_IsShownWhereItIsNow()
    {
        var (engine, _) = Build();
        var instanceId = SentToTheTeam(engine);

        var answer = TeamActs(engine, instanceId, "close");

        answer.ResponseState.Should().Be("complete");
        answer.Render!.StateDisplayName.Should().Be("Closed");
    }

    // The visitor waits at a join while the team replies: the reply completes the join and the
    // conversation carries on in the visitor's lane only.
    private const string JoinBlueprint = """
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
            displayName: Message
            queueKey: visitor
            components:
              - type: conversation
                fieldKey: conversation
                label: Your message
                required: true
            routes:
              - id: message--send--to-team
                target: to-team
                trigger: send
          - stageKey: reply
            displayName: Reply
            queueKey: team
            components:
              - type: conversation
                fieldKey: conversation
                label: Your reply
            routes:
              - id: reply--reply--awaiting-reply
                target: awaiting-reply
                trigger: reply
          - stageKey: follow-up
            displayName: Your conversation
            queueKey: visitor
            components:
              - type: conversation
                fieldKey: conversation
                label: Reply to us
        gateways:
          - key: to-team
            displayName: Send
            gatewayType: Split
            queueKey: visitor
            routes:
              - id: to-team--send--reply
                target: reply
                trigger: send
              - id: to-team--send--awaiting-reply
                target: awaiting-reply
                trigger: send
          - key: awaiting-reply
            displayName: Thank you
            gatewayType: Join
            queueKey: visitor
            routes:
              - id: awaiting-reply--reply--follow-up
                target: follow-up
                trigger: reply
            requiredIncomingQueues:
              - visitor
              - team
        """;

    [Fact]
    public void ATeamReplyThatCompletesTheVisitorsWait_IsAnsweredMovedOn()
    {
        var definition = ServiceBlueprintYaml.Deserialize(JoinBlueprint);
        var engine = new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: new InMemoryServiceRequestStore());
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "send", started.StateVersion, new() { ["conversation"] = "Hello" });

        var answer = TeamActs(engine, started.InstanceId, "reply", fieldKey: "conversation");

        answer.ResponseState.Should().Be("moved_on");
        answer.Problems.Should().BeEmpty();
        engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile, started.InstanceId).Render!.StateDisplayName.Should().Be("Your conversation");
    }

    [Fact]
    public void SomeoneElsesRequest_IsStillRefusedAsAccessDenied()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);

        var answer = engine.Advance(started.InstanceId, TenantId, "visitor-2", VisitorProfile, "send", started.StateVersion, new() { ["thread"] = "Hijack" });

        answer.ResponseState.Should().Be("error");
        answer.Problems.Should().ContainSingle(p => p.Code == "ACCESS_DENIED");
        store.TryGet(started.InstanceId, out var untouched).Should().BeTrue();
        untouched.CurrentStage.Should().Be("write");
    }
}
