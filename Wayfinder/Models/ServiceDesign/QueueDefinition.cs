using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public record QueueDefinition
{
    private string? _key;

    public string Key
    {
        get => _key ?? string.Empty;
        init => _key = value;
    }

    public string DisplayName { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    /// <summary>
    /// Which team/skill capabilities may pick up from or start work in this queue — an any-of list
    /// checked against <c>ActorProfile.Capabilities</c> (see <c>ProcessManagerEngine.HasQueueEligibility</c>).
    /// Null/empty (the default) means unrestricted, exactly matching every blueprint that predates
    /// this — the same convention <c>ActorProfile</c>'s own allow-lists already use. Distinct from
    /// <c>IQueueCapabilitiesProvider</c>'s unrelated, pre-existing use of the word "capability"
    /// (which component types a host can render for a queue) — see docs/guides/work-allocation.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RoleGates { get; init; }

    /// <summary>
    /// Null (the default) means no mandatory-assignment enforcement for this queue —
    /// <c>RequestCursor.AssignedTo</c> governs any optional pickup exactly as it did before this
    /// field existed. <c>"assign-to-initiator"</c>: whoever's action lands work here becomes its
    /// individual owner immediately. <c>"team-tray"</c>: work lands owned by <see cref="OwningTeamId"/>
    /// as a whole, pickable by any member, actionable only once picked up. Orthogonal to
    /// <see cref="RoleGates"/> — RoleGates governs eligibility to see/act on this queue at all;
    /// this governs who, among those already eligible, actually owns a given row. See
    /// docs/guides/team-assignment.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssignmentPolicy { get; init; }

    /// <summary>The team that owns this queue — only meaningful when <see cref="AssignmentPolicy"/> is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwningTeamId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
