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
/// When an anonymous visitor signs in, <see cref="ProcessManagerEngine.ClaimInstances"/> moves their
/// requests onto their account. A blueprint that lets a person have several keeps them all; a
/// one-per-person blueprint never silently drops the account's own. Only the visitor's own requests,
/// in that tenant, are ever moved.
/// </summary>
public class ClaimInstancesTests
{
    private const string TenantId = "tenant";
    private const string Anonymous = "anonymous-1";
    private const string Member = "member-1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"], StartableQueues = ["visitor"], ActionableQueues = ["visitor"], RestrictToInstanceOwner = true
    };

    private static string Blueprint(string requestPolicy) => $$"""
        {
          "definitionKey": "contact", "displayName": "Contact us", "version": 1,
          "initialStage": "write", "requestPolicy": "{{requestPolicy}}",
          "queues": [ { "key": "visitor", "displayName": "Visitor", "actor": "citizen" } ],
          "stages": [ { "stageKey": "write", "displayName": "Write", "queueKey": "visitor",
            "components": [ { "type": "text", "fieldKey": "message", "label": "Message" } ] } ]
        }
        """;

    private static (ProcessManagerEngine Engine, InMemoryServiceRequestStore Store) Build(string requestPolicy)
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(Blueprint(requestPolicy), JsonOptions)!;
        var store = new InMemoryServiceRequestStore();
        return (new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: store), store);
    }

    private static string Start(ProcessManagerEngine engine, string userId, string tenantId = TenantId) =>
        engine.GetCurrent("contact", tenantId, userId, VisitorProfile, action: "start-new").InstanceId;

    private static ServiceRequest Stored(InMemoryServiceRequestStore store, string instanceId)
    {
        store.TryGet(instanceId, out var instance).Should().BeTrue();
        return instance;
    }

    [Fact]
    public void ABlueprintAllowingSeveral_HasEveryAnonymousRequestClaimed_AlongsideTheMembersOwn()
    {
        var (engine, store) = Build("multiple");
        var membersOwn = Start(engine, Member);
        var first = Start(engine, Anonymous);
        var second = Start(engine, Anonymous);

        var claimed = engine.ClaimInstances(TenantId, Anonymous, Member);

        claimed.Should().BeEquivalentTo([first, second]);
        Stored(store, first).UserId.Should().Be(Member);
        Stored(store, second).IsAuthenticated.Should().BeTrue();
        Stored(store, membersOwn).UserId.Should().Be(Member);
    }

    [Fact]
    public void AOnePerPersonBlueprint_LeavesTheAnonymousRequestBehind_WhenTheMemberHasOne()
    {
        var (engine, store) = Build("single");
        var membersOwn = Start(engine, Member);
        var anonymous = Start(engine, Anonymous);

        var claimed = engine.ClaimInstances(TenantId, Anonymous, Member);

        claimed.Should().BeEmpty();
        Stored(store, anonymous).UserId.Should().Be(Anonymous);
        Stored(store, membersOwn).UserId.Should().Be(Member);
    }

    [Fact]
    public void OnlyTheVisitorsOwnRequests_InThatTenant_AreClaimed()
    {
        var (engine, store) = Build("multiple");
        var someoneElses = Start(engine, "anonymous-2");
        var otherTenants = Start(engine, Anonymous, tenantId: "other-tenant");
        var theirs = Start(engine, Anonymous);

        var claimed = engine.ClaimInstances(TenantId, Anonymous, Member);

        claimed.Should().Equal(theirs);
        Stored(store, someoneElses).UserId.Should().Be("anonymous-2");
        Stored(store, otherTenants).UserId.Should().Be(Anonymous);
    }
}
