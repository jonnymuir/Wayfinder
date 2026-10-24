using YamlDotNet.Serialization;
using System.Globalization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Something a stage or route does beyond just moving between them. Historically schema-only —
/// the engine copies an instance through wherever it's attached (<see cref="StageDefinition.Actions"/>,
/// <see cref="ServiceBlueprintRouteDefinition.Actions"/>) without ever executing it. The first
/// <see cref="Type"/> convention the engine actually executes is
/// <see cref="SupportSystems.SupportSystemActionTypes.SupportSystemCall"/> — see
/// docs/guides/support-systems.md.
/// </summary>
public record ActionDefinition
{
    public string Type { get; init; } = "";

    public required ActionTiming Timing { get; init; }

    [YamlMember(Alias = "parameterSchemaKey")]
    public string? ParameterSchemaKey { get; init; }

    public string? Summary { get; init; }

    [YamlMember(Alias = "params", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}
