using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Persisted service blueprint contract shared by authoring, seed files and runtime loading.
/// </summary>
public partial record ServiceBlueprint
{
    private IReadOnlyList<QueueDefinition>? _queues;

    public string DefinitionKey { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public int Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AuthoredServiceBlueprintId { get; init; }

    public string InitialStage { get; init; } = "";

    public string RequestPolicy { get; init; } = "single";

    /// <summary>
    /// Whether a citizen-facing surface may honour an explicit <c>action: "start-new"</c> request
    /// against this blueprint (e.g. a "Start again" link on a terminal Confirmation stage) —
    /// defaults to <see langword="false"/>. Unconditionally abandoning whatever instance a visitor
    /// currently has (even a genuinely in-progress one, e.g. mid-payment or mid-automation-decision)
    /// is meaningful enough — duplicate submissions, an orphaned in-flight instance nobody ever
    /// resolves — that a blueprint must opt in explicitly, rather than every blueprint getting it
    /// for free just because a host surface happens to expose the query-string action. Internal
    /// engine callers that need a guaranteed-fresh instance regardless of this flag (e.g.
    /// <see cref="Services.ServiceBlueprintSimulationRunner"/>, or a genuine admin abort/restart
    /// tool) call <see cref="Abstractions.IProcessManager"/>'s lower-level primitives directly and
    /// are unaffected — this flag only gates the untrusted, citizen-reachable path.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowManualRestart { get; init; }

    public IReadOnlyList<StageDefinition> Stages { get; init; } = Array.Empty<StageDefinition>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QueueDefinition>? Queues
    {
        get => _queues;
        init => _queues = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintGatewayDefinition>? Gateways { get; init; }

    /// <summary>
    /// Declarative calculations for this blueprint: tables, computed fields and series
    /// evaluated by <c>CalculationEvaluator</c> against instance field values plus
    /// host-supplied service inputs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Calculations.ServiceBlueprintCalculationSet? Calculations { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HandoffDefinition>? Handoffs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>
    /// Editor-owned canvas layout hints: manually arranged node positions
    /// keyed by prefixed node id (<c>stage:&lt;stageKey&gt;</c> /
    /// <c>gateway:&lt;key&gt;</c> — an opaque key from the runtime's perspective; nothing here
    /// parses or branches on the prefix). The runtime never reads this — it exists
    /// so authored arrangements survive the save/load round-trip.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceBlueprintLayoutDefinition? Layout { get; init; }

}
