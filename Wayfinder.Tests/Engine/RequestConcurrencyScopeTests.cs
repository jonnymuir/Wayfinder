using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="ActorProfile.ConcurrencyScopeKey"/> lets a host group "is there already an existing
/// instance?" by something other than the literal requesting user — e.g. one organisation's
/// several users all sharing one in-flight submission — without touching blueprint JSON at all
/// (a field-ref can't work here: a brand-new instance's FieldValues is empty until its first
/// submission, but this decision happens before that). Default (unset) must reproduce today's
/// exact per-user "single" behaviour for every existing caller.
/// </summary>
public class RequestConcurrencyScopeTests
{
    private const string TenantId = "tenant";
    private const string DefinitionKey = "concurrency-scope-test";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // A single-stage, single-policy ("single", the default) blueprint — just enough to exercise
    // FindLatestInstance's own matching, nothing about the concurrency-scope question depends on
    // anything more elaborate than "does GetCurrent return the same or a different instance".
    private const string BlueprintJson = """
        {
          "definitionKey": "concurrency-scope-test",
          "displayName": "Concurrency Scope Test",
          "version": 1,
          "initialStage": "only",
          "queues": [ { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" } ],
          "stages": [
            {
              "stageKey": "only",
              "displayName": "Only",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Only stage" } ]
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

    [Fact]
    public void UnsetConcurrencyScopeKey_TwoDifferentUsers_GetTwoSeparateInstances()
    {
        var engine = BuildEngine();

        var first = engine.GetCurrent(DefinitionKey, TenantId, "alex", ActorProfile.UnrestrictedOwner);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "priya", ActorProfile.UnrestrictedOwner);

        second.InstanceId.Should().NotBe(first.InstanceId, "today's default behaviour scopes by the literal userId");
    }

    [Fact]
    public void UnsetConcurrencyScopeKey_SameUserRevisiting_GetsTheSameInstance()
    {
        var engine = BuildEngine();

        var first = engine.GetCurrent(DefinitionKey, TenantId, "alex", ActorProfile.UnrestrictedOwner);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "alex", ActorProfile.UnrestrictedOwner);

        second.InstanceId.Should().Be(first.InstanceId, "unchanged regression: this is today's exact 'single' behaviour");
    }

    [Fact]
    public void SharedConcurrencyScopeKey_TwoDifferentUsers_CollapseToTheSameInstance()
    {
        var engine = BuildEngine();
        var orgProfile = new ActorProfile { ConcurrencyScopeKey = "org:njf" };

        var first = engine.GetCurrent(DefinitionKey, TenantId, "priya", orgProfile);
        var second = engine.GetCurrent(DefinitionKey, TenantId, "raj", orgProfile);

        second.InstanceId.Should().Be(first.InstanceId,
            "two different users sharing one ConcurrencyScopeKey must be treated as the same owner for 'single'");
    }

    [Fact]
    public void SharedConcurrencyScopeKey_AttributionIsStillPreserved()
    {
        // Losing "who actually did this" would be a real regression for a toolkit that's
        // otherwise careful about audit trails (see bulk-data-review's own correction overlay).
        var engine = BuildEngine();
        var orgProfile = new ActorProfile { ConcurrencyScopeKey = "org:njf" };

        var created = engine.GetCurrent(DefinitionKey, TenantId, "priya", orgProfile);
        var instance = engine.GetAllInstances().Single(i => i.InstanceId == created.InstanceId);

        instance.UserId.Should().Be("priya");
        instance.ConcurrencyScopeKey.Should().Be("org:njf");
    }

    [Fact]
    public void DifferentConcurrencyScopeKeys_DoNotCollapse()
    {
        var engine = BuildEngine();

        var first = engine.GetCurrent(DefinitionKey, TenantId, "priya", new ActorProfile { ConcurrencyScopeKey = "org:njf" });
        var second = engine.GetCurrent(DefinitionKey, TenantId, "sam", new ActorProfile { ConcurrencyScopeKey = "org:other-federation" });

        second.InstanceId.Should().NotBe(first.InstanceId);
    }
}
