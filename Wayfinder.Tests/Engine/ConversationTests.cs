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
/// A conversation field keeps every message: each submission adds to the end of the thread instead of
/// replacing it, both sides see who wrote what, and nothing a rejected or empty submission carries ever
/// changes the thread. Driven through <see cref="ProcessManagerEngine"/> with a real in-memory store.
/// </summary>
public class ConversationTests
{
    private const string TenantId = "tenant";
    private const string Visitor = "anonymous-visitor-1";
    private const string Caseworker = "caseworker-1";


    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"], StartableQueues = ["visitor"], ActionableQueues = ["visitor"], RestrictToInstanceOwner = true
    };

    private static readonly ActorProfile TeamProfile = new()
    {
        VisibleQueues = ["team"], StartableQueues = [], ActionableQueues = ["team"], RestrictToInstanceOwner = false
    };

    // Contact us: the visitor writes, the team replies or closes, the visitor can write again.
    private const string ContactBlueprint = """
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
            displayName: The Exchange
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
                maxLength: 200
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
              - type: summary-list
                title: Latest
                children:
                  - type: text
                    fieldKey: thread
                    label: Latest message
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
        var definition = ServiceBlueprintYaml.Deserialize(ContactBlueprint);
        var store = new InMemoryServiceRequestStore();
        return (new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: store), store);
    }

    private static Dictionary<string, object?> Message(string text) => new() { ["thread"] = text };

    private static IReadOnlyList<ConversationEntry> StoredThread(InMemoryServiceRequestStore store, string instanceId)
    {
        store.TryGet(instanceId, out var instance).Should().BeTrue();
        return ConversationEntry.ReadThread(instance.FieldValues.GetValueOrDefault("thread"));
    }

    private static FieldRenderPayload ConversationField(ServiceRequestResponseEnvelope envelope) =>
        envelope.Render!.Components.SelectMany(c => c.Fields).Single(f => f.FieldType == "conversation");

    private static ServiceRequestResponseEnvelope VisitorSends(ProcessManagerEngine engine, string instanceId, string text)
    {
        var view = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile, instanceId);
        return engine.Advance(instanceId, TenantId, Visitor, VisitorProfile, "send", view.StateVersion, Message(text));
    }

    private static ServiceRequestResponseEnvelope TeamActs(ProcessManagerEngine engine, string instanceId, string action, string text)
    {
        var item = engine.GetQueueWorkItems(TenantId, Caseworker, TeamProfile).Items.Single(i => i.InstanceId == instanceId);
        var pickedUp = engine.PickupWorkItem(instanceId, item.CursorId, TenantId, Caseworker, TeamProfile);
        return engine.Advance(instanceId, TenantId, Caseworker, TeamProfile, action, pickedUp.StateVersion, Message(text));
    }

    private static ServiceRequestResponseEnvelope TeamView(ProcessManagerEngine engine, string instanceId)
    {
        var item = engine.GetQueueWorkItems(TenantId, Caseworker, TeamProfile).Items.Single(i => i.InstanceId == instanceId);
        return engine.PickupWorkItem(instanceId, item.CursorId, TenantId, Caseworker, TeamProfile);
    }

    [Fact]
    public void EachMessage_IsAddedToTheEnd_AndNothingIsOverwritten()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);

        VisitorSends(engine, started.InstanceId, "Hello, are you open on Sunday?");
        TeamActs(engine, started.InstanceId, "reply", "Yes, 10am to 4pm.");
        VisitorSends(engine, started.InstanceId, "Thanks!");

        var thread = StoredThread(store, started.InstanceId);
        thread.Select(e => e.Text).Should().Equal("Hello, are you open on Sunday?", "Yes, 10am to 4pm.", "Thanks!");
        thread.Select(e => e.QueueKey).Should().Equal("visitor", "team", "visitor");
        thread.Select(e => e.At).Should().BeInAscendingOrder();
    }

    [Fact]
    public void EachSide_SeesItsOwnMessagesAsYou_AndTheOtherSideByItsQueueName()
    {
        var (engine, _) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "Hello");

        var teamSees = ConversationField(TeamView(engine, started.InstanceId)).Conversation!;
        teamSees.Single().AuthorLabel.Should().Be("Visitor");
        teamSees.Single().IsOwnSide.Should().BeFalse();

        TeamActs(engine, started.InstanceId, "reply", "Hi");
        var visitorSees = ConversationField(engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile, started.InstanceId)).Conversation!;
        visitorSees.Select(m => m.AuthorLabel).Should().Equal("You", "The Exchange");
        visitorSees.Select(m => m.IsOwnSide).Should().Equal(true, false);
    }

    [Fact]
    public void TheBoxForTheNextMessage_StartsEmpty()
    {
        var (engine, _) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "Hello");

        ConversationField(TeamView(engine, started.InstanceId)).Value.Should().Be("");
    }

    [Fact]
    public void AnEmptyMessage_LeavesTheThreadAsItWas()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "Hello");

        TeamActs(engine, started.InstanceId, "close", "   ");

        StoredThread(store, started.InstanceId).Select(e => e.Text).Should().Equal("Hello");
    }

    [Fact]
    public void ARequiredMessage_MustBeWritten_AndARejectionSavesNothing()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);

        var rejected = engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "send", started.StateVersion, Message(""));

        rejected.Problems.Should().ContainSingle(p => p.FieldKey == "thread" && p.Code == "VALIDATION_ERROR");
        store.TryGet(started.InstanceId, out var unchanged).Should().BeTrue();
        unchanged.StateVersion.Should().Be(started.StateVersion);
        unchanged.CurrentStage.Should().Be("write");
    }

    [Fact]
    public void AMessageOverTheLimit_IsRejected_AndTheThreadStillShows()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "Hello");
        TeamActs(engine, started.InstanceId, "reply", "Hi");
        var view = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile, started.InstanceId);

        var rejected = engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "send", view.StateVersion, Message(new string('x', 201)));

        rejected.Problems.Should().ContainSingle(p => p.FieldKey == "thread");
        ConversationField(rejected).Conversation!.Select(m => m.Text).Should().Equal("Hello", "Hi");
        StoredThread(store, started.InstanceId).Should().HaveCount(2);
    }

    [Fact]
    public void AMessageWithNoLimitSet_IsBoundedByTheDefault()
    {
        var (engine, store) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "Hello");

        var rejected = TeamActs(engine, started.InstanceId, "reply", new string('x', Wayfinder.Models.ServiceDesign.Components.ConversationComponent.DefaultMaxLength + 1));

        rejected.Problems.Should().ContainSingle(p => p.FieldKey == "thread");
        StoredThread(store, started.InstanceId).Should().ContainSingle();
    }

    [Fact]
    public void ASummaryRowEchoingTheConversation_ShowsTheLatestMessage()
    {
        var (engine, _) = Build();
        var started = engine.GetCurrent("contact", TenantId, Visitor, VisitorProfile);
        VisitorSends(engine, started.InstanceId, "First");
        TeamActs(engine, started.InstanceId, "reply", "Reply");
        VisitorSends(engine, started.InstanceId, "Second");

        var summary = TeamView(engine, started.InstanceId).Render!.Components.Single(c => c.Type == "summary-list");

        summary.Fields.Single().Value.Should().Be("Second");
    }

    [Fact]
    public void AThreadReadBackFromJson_KeepsEveryMessage()
    {
        var stored = new[]
        {
            new ConversationEntry { Text = "Hello", QueueKey = "visitor", At = DateTimeOffset.Parse("2026-10-18T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
            new ConversationEntry { Text = "Hi", QueueKey = "team", At = DateTimeOffset.Parse("2026-10-18T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
        };
        var roundTripped = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(stored));

        ConversationEntry.ReadThread(roundTripped).Should().BeEquivalentTo(stored);
    }
}
