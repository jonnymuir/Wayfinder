using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// When an <see cref="ActionDefinition"/> runs. Only <see cref="OnEnter"/> is executed by the
/// engine today (support-system and bulk-dataset actions); the others are authored but schema-only.
/// Serialized camelCase: <c>"onEnter"</c>, <c>"onExit"</c>, <c>"onTransition"</c>.
/// </summary>
[JsonConverter(typeof(ActionTimingJsonConverter))]
public enum ActionTiming
{
    /// <summary>When the stage the action is attached to is entered.</summary>
    OnEnter,

    /// <summary>When the stage the action is attached to is left.</summary>
    OnExit,

    /// <summary>When the route the action is attached to is taken.</summary>
    OnTransition,
}

/// <summary>Writes <see cref="ActionTiming"/> camelCased and refuses numbers.</summary>
public sealed class ActionTimingJsonConverter()
    : JsonStringEnumConverter<ActionTiming>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
