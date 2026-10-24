using YamlDotNet.Serialization;
using System.Globalization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public record ServiceBlueprintGatewayDefinition
{
    private string? _queueKey;

    public string Key { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string? Description { get; init; }

    public required GatewayKind GatewayType { get; init; }

    public string QueueKey
    {
        get => _queueKey ?? string.Empty;
        init => _queueKey = value;
    }

    public string? Actor { get; init; }

    public IReadOnlyList<string>? RoleGates { get; init; }

    public IReadOnlyList<ServiceBlueprintRouteDefinition>? Routes { get; init; }

    public string? WaitingContent { get; init; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public int WaitingExpectedSeconds { get; init; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public int WaitingPollIntervalMs { get; init; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool WaitingAllowDefer { get; init; }

    public string? WaitingDeferMessage { get; init; }

    public IReadOnlyList<string>? RequiredIncomingQueues { get; init; }

    /// <summary>Curated icon-set key (see the client's graph/node-icons.ts). Falls back to a Split/Join default when unset.</summary>
    public string? Icon { get; init; }
}
