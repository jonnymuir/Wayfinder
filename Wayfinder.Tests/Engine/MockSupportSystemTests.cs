using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Extensions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A support system configured with <c>mock: true</c> answers from configuration, so a prototype
/// runs before the real system exists and swapping it in is a configuration change. See
/// docs/guides/support-systems.md.
/// </summary>
public class MockSupportSystemTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";
    private const string DefinitionKey = "member-lookup";

    private static readonly ActorProfile CaseworkerProfile = new()
    {
        VisibleQueues = ["caseworker"],
        StartableQueues = ["caseworker"],
        ActionableQueues = ["caseworker"],
        RestrictToInstanceOwner = false,
    };

    private const string BlueprintYaml = """
        definitionKey: member-lookup
        displayName: Member lookup
        version: 1
        initialStage: start
        requestPolicy: single
        queues:
          - key: caseworker
            displayName: Caseworker
            actor: caseworker
          - key: automation
            displayName: Automation
            actor: system
        stages:
          - stageKey: start
            displayName: Start
            queueKey: caseworker
            components:
              - type: text
                fieldKey: memberNumber
                label: Member number
            routes:
              - id: start--look-up--split
                target: to-business-system
                trigger: look-up
          - stageKey: looking-up
            displayName: Looking up
            queueKey: automation
            actions:
              - type: support-system-call
                timing: onEnter
                params:
                  supportSystemKey: business-system
                  capabilityKey: get-member
                  inputs:
                    memberNumber: memberNumber
            components:
              - type: panel
                heading: Looking up
            routes:
              - id: looking-up--found--join
                target: lookup-complete
                trigger: found
              - id: looking-up--not-found--join
                target: lookup-complete
                trigger: not-found
          - stageKey: member
            displayName: Member
            queueKey: caseworker
            components:
              - type: summary-list
                title: Member
                children:
                  - type: text
                    fieldKey: memberName
                    label: Name
          - stageKey: no-member
            displayName: No member
            queueKey: caseworker
            components:
              - type: panel
                heading: No member found
        gateways:
          - key: to-business-system
            displayName: Ask the business system
            gatewayType: Split
            queueKey: caseworker
            routes:
              - id: to-business-system--look-up--join
                target: lookup-complete
                trigger: look-up
              - id: to-business-system--look-up--lookup
                target: looking-up
                trigger: look-up
          - key: lookup-complete
            displayName: Lookup complete
            gatewayType: Join
            queueKey: caseworker
            waitingContent: Looking up the member.
            routes:
              - id: lookup-complete--found--member
                target: member
                trigger: found
              - id: lookup-complete--not-found--no-member
                target: no-member
                trigger: not-found
            requiredIncomingQueues:
              - caseworker
              - automation
        """;

    private static Dictionary<string, string?> MockSettings() => new()
    {
        ["Wayfinder:SupportSystems:0:key"] = "business-system",
        ["Wayfinder:SupportSystems:0:displayName"] = "Business system (mock)",
        ["Wayfinder:SupportSystems:0:mock"] = "true",
        ["Wayfinder:SupportSystems:0:capabilities:0:key"] = "get-member",
        ["Wayfinder:SupportSystems:0:capabilities:0:inputs:0:key"] = "memberNumber",
        ["Wayfinder:SupportSystems:0:capabilities:0:outputs:0:key"] = "memberName",
        ["Wayfinder:SupportSystems:0:capabilities:0:outputs:1:key"] = "serviceYears",
        ["Wayfinder:SupportSystems:0:capabilities:0:outputs:1:valueKind"] = "Integer",
        ["Wayfinder:SupportSystems:0:capabilities:0:outcomes:0:key"] = "found",
        ["Wayfinder:SupportSystems:0:capabilities:0:outcomes:1:key"] = "not-found",
        ["Wayfinder:SupportSystems:0:capabilities:0:mock:outcome"] = "found",
        ["Wayfinder:SupportSystems:0:capabilities:0:mock:outputs:memberName"] = "Alex Morgan",
        ["Wayfinder:SupportSystems:0:capabilities:0:mock:outputs:serviceYears"] = "12",
        ["Wayfinder:SupportSystems:0:capabilities:0:mock:cases:0:when:memberNumber"] = "00000000",
        ["Wayfinder:SupportSystems:0:capabilities:0:mock:cases:0:outcome"] = "not-found",
    };

    private static ServiceProvider Register(Dictionary<string, string?> settings)
    {
        SupportSystemRegistry.ResetForTests();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguredSupportSystems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    private static ServiceRequest LookUp(ServiceProvider provider, string memberNumber, out string stageName)
    {
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(ServiceBlueprintYaml.Deserialize(BlueprintYaml)),
            new PassthroughContentSanitizer(),
            supportSystemClients: provider.GetServices<ISupportSystemClient>());

        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile);
        var pickedUp = engine.PickupWorkItem(started.InstanceId, RequestCursor.PrimaryCursorId, TenantId, UserId, CaseworkerProfile);
        engine.Advance(
            started.InstanceId, TenantId, UserId, CaseworkerProfile, "look-up", pickedUp.StateVersion,
            new Dictionary<string, object?> { ["memberNumber"] = memberNumber });

        stageName = engine.GetCurrent(DefinitionKey, TenantId, UserId, CaseworkerProfile, started.InstanceId).Render!.StateDisplayName;
        return engine.GetAllInstances().Single(i => i.InstanceId == started.InstanceId);
    }

    [Fact]
    public void AMockNeedsNoEndpoint_AndIsCatalogedAsAnsweringWithoutAWebhook()
    {
        try
        {
            using var provider = Register(MockSettings());

            var capability = SupportSystemRegistry.FindCapability("business-system", "get-member");
            capability!.SupportedCompletionModes.Should().Equal(SupportSystemCompletionMode.Poll);
            provider.GetServices<ISupportSystemClient>().Should().ContainSingle(c => c.SupportSystemKey == "business-system");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void TheJourneyCarriesOnWithTheDefaultAnswer_AndItsOutputsInTheRequest()
    {
        try
        {
            using var provider = Register(MockSettings());

            var request = LookUp(provider, "12345678", out var stageName);

            stageName.Should().Be("Member");
            request.FieldValues["memberName"].Should().Be("Alex Morgan");
            request.FieldValues["serviceYears"]!.ToString().Should().Be("12");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ACaseMatchingTheInputs_GivesItsOwnAnswer()
    {
        try
        {
            using var provider = Register(MockSettings());

            var request = LookUp(provider, "00000000", out var stageName);

            stageName.Should().Be("No member");
            request.FieldValues.Should().NotContainKey("memberName");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Theory]
    [InlineData("Wayfinder:SupportSystems:0:capabilities:0:mock:outcome", "approved", "*outcome 'approved'*not one of its declared outcomes*")]
    [InlineData("Wayfinder:SupportSystems:0:capabilities:0:mock:outputs:nickname", "Al", "*output 'nickname'*not one of its declared outputs*")]
    [InlineData("Wayfinder:SupportSystems:0:capabilities:0:mock:cases:0:when:surname", "Morgan", "*matches on 'surname'*not one of its declared inputs*")]
    [InlineData("Wayfinder:SupportSystems:0:capabilities:0:mock:outputs:serviceYears", "twelve", "*'twelve'*not a valid Integer*")]
    public void AMockAnswerThatDoesNotFitItsCapability_FailsAtStartup(string key, string value, string message)
    {
        try
        {
            var settings = MockSettings();
            settings[key] = value;

            var act = () => Register(settings);

            act.Should().Throw<InvalidOperationException>().WithMessage(message);
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void AMockedCapabilityWithNoAnswer_FailsAtStartup()
    {
        try
        {
            var settings = MockSettings()
                .Where(s => !s.Key.Contains(":mock:", StringComparison.Ordinal))
                .ToDictionary(s => s.Key, s => s.Value);

            var act = () => Register(settings);

            act.Should().Throw<InvalidOperationException>().WithMessage("*'get-member' has no 'mock' answer*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
