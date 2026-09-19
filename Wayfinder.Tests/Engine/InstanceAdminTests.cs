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
/// <see cref="ProcessManagerEngine.SearchInstancesForAdmin"/> and
/// <see cref="ProcessManagerEngine.AbortInstance"/> — the cross-tenant admin surface built to
/// diagnose and stop a stuck instance (found live: a production instance permanently parked at a
/// join gateway waiting on an automation whose trigger URL was misconfigured, with no way to see
/// or clear it). Abort is a soft-terminate (audit trail preserved), never a delete.
/// </summary>
public class InstanceAdminTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "admin-search-test",
          "displayName": "Admin Search Test",
          "version": 1,
          "initialStage": "start",
          "requestPolicy": "multiple",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "start",
              "displayName": "Start",
              "queueKey": "caseworker",
              "components": [ { "type": "text", "fieldKey": "notes", "label": "Notes", "required": false } ],
              "routes": [ { "id": "start--continue--done", "target": "done", "trigger": "continue" } ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Done" } ]
            }
          ]
        }
        """;

    private static ProcessManagerEngine BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    private static string StartInstance(ProcessManagerEngine engine, string tenantId, string userId) =>
        engine.GetCurrent("admin-search-test", tenantId, userId, ActorProfile.UnrestrictedOwner).InstanceId;

    [Fact]
    public void Search_ByDefault_ExcludesAbortedInstances()
    {
        var engine = BuildEngine();
        var kept = StartInstance(engine, TenantA, "user-1");
        var aborted = StartInstance(engine, TenantA, "user-2");
        engine.AbortInstance(aborted, "test cleanup", "admin@example.com").Should().BeTrue();

        var result = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery());

        result.Items.Select(i => i.InstanceId).Should().Contain(kept);
        result.Items.Select(i => i.InstanceId).Should().NotContain(aborted);
    }

    [Fact]
    public void Search_IncludeAborted_ReturnsBoth()
    {
        var engine = BuildEngine();
        var kept = StartInstance(engine, TenantA, "user-1");
        var aborted = StartInstance(engine, TenantA, "user-2");
        engine.AbortInstance(aborted, "test cleanup", "admin@example.com");

        var result = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { IncludeAborted = true });

        result.Items.Select(i => i.InstanceId).Should().Contain([kept, aborted]);
        var abortedRow = result.Items.Single(i => i.InstanceId == aborted);
        abortedRow.IsAborted.Should().BeTrue();
        abortedRow.AbortedReason.Should().Be("test cleanup");
        abortedRow.AbortedByUserId.Should().Be("admin@example.com");
        abortedRow.AbortedAt.Should().NotBeNull();
    }

    [Fact]
    public void Search_IsGenuinelyCrossTenant_NotScopedToOneTenantByDefault()
    {
        var engine = BuildEngine();
        var inTenantA = StartInstance(engine, TenantA, "user-1");
        var inTenantB = StartInstance(engine, TenantB, "user-1");

        var everything = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery());
        var scopedToA = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { TenantId = TenantA });

        everything.Items.Select(i => i.InstanceId).Should().Contain([inTenantA, inTenantB]);
        scopedToA.Items.Select(i => i.InstanceId).Should().ContainSingle(id => id == inTenantA);
    }

    [Fact]
    public void Search_TextMatchesInstanceIdAndUserId()
    {
        var engine = BuildEngine();
        var instanceId = StartInstance(engine, TenantA, "distinctive-user-id");

        var byInstanceId = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { SearchText = instanceId });
        var byUserId = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { SearchText = "distinctive-user" });
        var byNothingMatching = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { SearchText = "no-such-thing-at-all" });

        byInstanceId.Items.Should().ContainSingle(i => i.InstanceId == instanceId);
        byUserId.Items.Should().ContainSingle(i => i.InstanceId == instanceId);
        byNothingMatching.Items.Should().BeEmpty();
    }

    [Fact]
    public void Search_DefaultSort_IsStalestFirst()
    {
        var engine = BuildEngine();
        var older = StartInstance(engine, TenantA, "user-older");
        // Advancing "newer" bumps its UpdatedAt to now, strictly after "older"'s untouched CreatedAt/UpdatedAt.
        var newer = StartInstance(engine, TenantA, "user-newer");
        var startedNewer = engine.GetCurrent("admin-search-test", TenantA, "user-newer", ActorProfile.UnrestrictedOwner);
        engine.Advance(newer, TenantA, "user-newer", "continue", startedNewer.StateVersion, null);

        var result = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery());

        var ids = result.Items.Select(i => i.InstanceId).ToList();
        ids.IndexOf(older).Should().BeLessThan(ids.IndexOf(newer), "the least-recently-touched instance — most likely to be stuck — must surface first");
    }

    [Fact]
    public void Search_Paging_ReportsTotalMatchingCountAcrossPages()
    {
        var engine = BuildEngine();
        for (var i = 0; i < 5; i++)
        {
            StartInstance(engine, TenantA, $"user-{i}");
        }

        var firstPage = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { PageIndex = 0, PageSize = 2 });
        var secondPage = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { PageIndex = 1, PageSize = 2 });

        firstPage.Items.Should().HaveCount(2);
        secondPage.Items.Should().HaveCount(2);
        firstPage.TotalMatchingCount.Should().Be(5);
        firstPage.Items.Select(i => i.InstanceId).Should().NotIntersectWith(secondPage.Items.Select(i => i.InstanceId));
    }

    [Fact]
    public void AbortInstance_UnknownInstance_ReturnsFalse()
    {
        var engine = BuildEngine();

        engine.AbortInstance("does-not-exist", "reason", "admin").Should().BeFalse();
    }

    [Fact]
    public void AbortInstance_IsIdempotent_KeepsOriginalAuditTrail()
    {
        var engine = BuildEngine();
        var instanceId = StartInstance(engine, TenantA, "user-1");

        engine.AbortInstance(instanceId, "first reason", "first-admin").Should().BeTrue();
        engine.AbortInstance(instanceId, "second reason", "second-admin").Should().BeTrue();

        var summary = engine.SearchInstancesForAdmin(new ServiceRequestAdminQuery { IncludeAborted = true })
            .Items.Single(i => i.InstanceId == instanceId);
        summary.AbortedReason.Should().Be("first reason", "a second abort attempt must not overwrite who/why the first one already recorded");
        summary.AbortedByUserId.Should().Be("first-admin");
    }

    [Fact]
    public void AbortInstance_EveryRenderAndAdvanceAttemptAfterwards_ReturnsTheStoppedError()
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent("admin-search-test", TenantA, "user-1", ActorProfile.UnrestrictedOwner);
        var instanceId = started.InstanceId;

        engine.AbortInstance(instanceId, "citizen requested cancellation", "admin@example.com");

        var rendered = engine.TryGetAccessibleInstance(instanceId, TenantA, "user-1", ActorProfile.UnrestrictedOwner);
        rendered.Should().NotBeNull("TryGetAccessibleInstance is unaffected by abort — it's a raw store read, not a render/advance");
        rendered!.IsAborted.Should().BeTrue();

        // Explicit instanceId, not ambient — "multiple" policy always creates a fresh instance on
        // an ambient (no-instanceId) render, which would sidestep the very instance under test.
        var render = engine.GetCurrent("admin-search-test", TenantA, "user-1", ActorProfile.UnrestrictedOwner, instanceId);
        render.ResponseState.Should().Be("error");
        render.Problems.Should().ContainSingle(p => p.Code == "INSTANCE_ABORTED");

        var advanceAttempt = engine.Advance(instanceId, TenantA, "user-1", "continue", started.StateVersion, null);
        advanceAttempt.ResponseState.Should().Be("error");
        advanceAttempt.Problems.Should().ContainSingle(p => p.Code == "INSTANCE_ABORTED");
    }
}
