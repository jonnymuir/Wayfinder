using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Editor canvas layout hints. Positions are whole flow pixels; queue
/// membership stays authoritative on the stages/gateways themselves.
/// </summary>
public record ServiceBlueprintLayoutDefinition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, NodePosition>? Nodes { get; init; }

    /// <summary>
    /// Manual bend point per route edge, keyed by the same "fromId->toId"
    /// edge key the canvas uses for its graph edges. Only set once an author
    /// drags a route; absent routes fall back to the auto-computed path.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, NodePosition>? Routes { get; init; }
}
