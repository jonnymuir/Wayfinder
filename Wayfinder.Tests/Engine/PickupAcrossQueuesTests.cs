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
/// SECURITY REGRESSION (availability: work stranded out of reach). A pickup belongs to one stay in one
/// queue. When a plain stage-to-stage hop moves the work into a different queue, the pickup does not go
/// with it: otherwise the request stays assigned to someone in the queue it left, and nobody in the
/// queue it arrived in, including the person it was sent back to, can see it.
/// </summary>
public class PickupAcrossQueuesTests
{
    private const string TenantId = "tenant";
    private const string Visitor = "visitor-1";
    private const string Caseworker = "caseworker-1";
    private const string Reviewer = "reviewer-1";


    private static readonly ActorProfile VisitorProfile = new()
    {
        VisibleQueues = ["visitor"], StartableQueues = ["visitor"], ActionableQueues = ["visitor"], RestrictToInstanceOwner = true
    };

    private static readonly ActorProfile CaseworkProfile = new()
    {
        VisibleQueues = ["casework"], StartableQueues = [], ActionableQueues = ["casework"], RestrictToInstanceOwner = false
    };

    private static readonly ActorProfile ReviewProfile = new()
    {
        VisibleQueues = ["review"], StartableQueues = [], ActionableQueues = ["review"], RestrictToInstanceOwner = false
    };

    private const string Blueprint = """
        definitionKey: application
        displayName: Application
        version: 1
        initialStage: apply
        requestPolicy: single
        queues:
          - key: visitor
            displayName: Applicant
            actor: citizen
          - key: casework
            displayName: Casework
            actor: caseworker
          - key: review
            displayName: Review
            actor: caseworker
        stages:
          - stageKey: apply
            displayName: Apply
            queueKey: visitor
            components:
              - type: text
                fieldKey: detail
                label: Detail
            routes:
              - id: apply--submit--assess
                target: assess
                trigger: submit
          - stageKey: assess
            displayName: Assess
            queueKey: casework
            components:
              - type: panel
                heading: Assess
            routes:
              - id: assess--more--apply
                target: apply
                trigger: need-more
              - id: assess--escalate--check
                target: check
                trigger: escalate
          - stageKey: check
            displayName: Check
            queueKey: review
            components:
              - type: text
                fieldKey: verdict
                label: Verdict
            routes:
              - id: check--approve--done
                target: done
                trigger: approve
          - stageKey: done
            displayName: Done
            queueKey: review
            components:
              - type: panel
                heading: Done
        """;

    private static ProcessManagerEngine Build()
    {
        var definition = ServiceBlueprintYaml.Deserialize(Blueprint);
        return new ProcessManagerEngine(NullLogger.Instance, new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(), instanceStore: new InMemoryServiceRequestStore());
    }

    private static string SubmittedAndPickedUpByCasework(ProcessManagerEngine engine, out int stateVersion)
    {
        var started = engine.GetCurrent("application", TenantId, Visitor, VisitorProfile);
        engine.Advance(started.InstanceId, TenantId, Visitor, VisitorProfile, "submit", started.StateVersion, null);
        var item = engine.GetQueueWorkItems(TenantId, Caseworker, CaseworkProfile).Items.Single(i => i.InstanceId == started.InstanceId);
        stateVersion = engine.PickupWorkItem(started.InstanceId, item.CursorId, TenantId, Caseworker, CaseworkProfile).StateVersion;
        return started.InstanceId;
    }

    [Fact]
    public void WorkSentBackToTheApplicant_IsTheirsToSeeAndActOnAgain()
    {
        var engine = Build();
        var instanceId = SubmittedAndPickedUpByCasework(engine, out var version);

        engine.Advance(instanceId, TenantId, Caseworker, CaseworkProfile, "need-more", version, null);

        var applicantView = engine.GetCurrent("application", TenantId, Visitor, VisitorProfile, instanceId);
        applicantView.ResponseState.Should().Be("render");
        applicantView.Render!.StateDisplayName.Should().Be("Apply");
        applicantView.Render.AvailableActions.Select(a => a.ActionKey).Should().Contain("submit");
    }

    [Fact]
    public void WorkHandedToAnotherTeam_IsInThatTeamsQueue_ForThemToPickUp()
    {
        var engine = Build();
        var instanceId = SubmittedAndPickedUpByCasework(engine, out var version);

        engine.Advance(instanceId, TenantId, Caseworker, CaseworkProfile, "escalate", version, null);

        var reviewItem = engine.GetQueueWorkItems(TenantId, Reviewer, ReviewProfile).Items.Should()
            .ContainSingle(i => i.InstanceId == instanceId, "it arrived in the review queue unassigned, for a reviewer to pick up").Subject;
        engine.PickupWorkItem(instanceId, reviewItem.CursorId, TenantId, Reviewer, ReviewProfile).ResponseState.Should().Be("render");
    }
}
