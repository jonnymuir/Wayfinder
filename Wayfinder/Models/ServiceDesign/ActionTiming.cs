using System.Runtime.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// When an <see cref="ActionDefinition"/> runs. Only <see cref="OnEnter"/> is executed by the
/// engine today (support-system and bulk-dataset actions); the others are authored but schema-only.
/// Written camelCase: <c>onEnter</c>, <c>onExit</c>, <c>onTransition</c>.
/// </summary>
public enum ActionTiming
{
    /// <summary>When the stage the action is attached to is entered.</summary>
    [EnumMember(Value = "onEnter")]
    OnEnter,

    /// <summary>When the stage the action is attached to is left.</summary>
    [EnumMember(Value = "onExit")]
    OnExit,

    /// <summary>When the route the action is attached to is taken.</summary>
    [EnumMember(Value = "onTransition")]
    OnTransition,
}
