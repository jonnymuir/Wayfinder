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
/// A <c>source: "service"</c> field the host doesn't supply is read from the request's stored
/// values, so a host's resolver only has to supply what it actually looks up. The engine's own
/// actions write some service values (a bulk-dataset ingest's counts), and before this every host
/// had to pass those through itself or the calculation failed.
/// </summary>
public class ServiceInputsStoredValueTests
{
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private const string BlueprintYaml = """
        definitionKey: stored-service-value
        displayName: Stored service value
        version: 1
        initialStage: start
        requestPolicy: single
        queues:
          - key: citizen
            displayName: Applicant
            actor: citizen
        stages:
          - stageKey: start
            displayName: Start
            queueKey: citizen
            components:
              - type: panel
                heading: Start
            routes: []
        gateways: []
        calculations:
          fields:
            count:
              source: service
            doubled:
              expr: count * 2
        """;

    [Fact]
    public void AServiceFieldTheHostDoesNotSupply_IsReadFromTheRequestsStoredValue()
    {
        var doubled = DoubledAfterStoring(count: 21m, hostSupplies: null);

        doubled.Should().Be(42m);
    }

    [Fact]
    public void AValueTheHostSupplies_WinsOverTheStoredValue()
    {
        var doubled = DoubledAfterStoring(count: 21m, hostSupplies: 5m);

        doubled.Should().Be(10m);
    }

    // Starts a request, writes `count` into its stored values the way an engine action would,
    // renders it again, and returns the calculated `doubled`.
    private static object? DoubledAfterStoring(decimal count, decimal? hostSupplies)
    {
        var definition = ServiceBlueprintYaml.Deserialize(BlueprintYaml);
        var store = new InMemoryServiceRequestStore();
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer(),
            serviceInputsResolver: hostSupplies is null
                ? null
                : (_, _, _) => new Dictionary<string, object?> { ["count"] = hostSupplies },
            instanceStore: store);

        var started = engine.GetCurrent(definition.DefinitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner);
        store.TryGet(started.InstanceId, out var request).Should().BeTrue();
        request.FieldValues["count"] = count;
        store.Save(request);

        engine.GetCurrent(definition.DefinitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner, started.InstanceId);

        return engine.GetLastCalculationResult(started.InstanceId)?.Fields["doubled"];
    }
}
