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

    /// <summary>When the answer this join waits for usually comes back. Unset gives a neutral page that makes no time claims.</summary>
    public WaitingPace? WaitingPace { get; init; }

    /// <summary>How long a <see cref="ServiceDesign.WaitingPace.WhileTheyWait"/> wait usually takes. Ignored for any other pace.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public int WaitingExpectedSeconds { get; init; }

    /// <summary>Overrides how often the page checks for the answer. Normally left unset, so the pace decides.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public int WaitingPollIntervalMs { get; init; }

    /// <summary>An optional small line icon shown in place of the plain "still working" dot. Unset (the default) shows none.</summary>
    public WaitingIcon? WaitingIcon { get; init; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool WaitingAllowDefer { get; init; }

    public string? WaitingDeferMessage { get; init; }

    public IReadOnlyList<string>? RequiredIncomingQueues { get; init; }

    /// <summary>Curated icon-set key (see the client's graph/node-icons.ts). Falls back to a Split/Join default when unset.</summary>
    public string? Icon { get; init; }
}
