using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public record StageDefinition
{
    private string? _queueKey;
    private IReadOnlyList<ServiceBlueprintRouteDefinition>? _routes;

    public string StageKey { get; init; } = "";

    public string DisplayName { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StageKind? StageType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    public string QueueKey
    {
        get => _queueKey ?? string.Empty;
        init => _queueKey = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }

    public IReadOnlyList<Component> Components { get; init; } = Array.Empty<Component>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintRouteDefinition>? Routes
    {
        get => _routes;
        init => _routes = value;
    }

    /// <summary>
    /// Declarative cross-field business rules checked before this stage is allowed to advance —
    /// see <see cref="ServiceBlueprintStageValidationRule"/>. Evaluated by
    /// <c>ProcessManagerEngine.Advance</c> after field-level validation passes, in addition to
    /// (and ahead of) the <c>ValidateAdvance</c> host hook.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceBlueprintStageValidationRule>? Validations { get; init; }

    /// <summary>Curated icon-set key (see the client's graph/node-icons.ts). Falls back to a stage-kind default when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

}
