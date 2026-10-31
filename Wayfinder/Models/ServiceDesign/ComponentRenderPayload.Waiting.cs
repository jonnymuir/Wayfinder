namespace Wayfinder.Models.ServiceDesign;

public partial record ComponentRenderPayload
{
    /// <summary>
    /// For a "waiting" component at a join: the display names of the queues it is still waiting on, kept
    /// apart from <see cref="Content"/> so a host decides whether and how to say who it is waiting for.
    /// </summary>
    public IReadOnlyList<string>? WaitingFor { get; init; }

    /// <summary>For a "waiting" component at a join: the join's authored pace. Null means a neutral page with no time claims.</summary>
    public WaitingPace? WaitingPace { get; init; }

    /// <summary>For a "waiting" component at a join: the join's optional line icon.</summary>
    public WaitingIcon? WaitingIcon { get; init; }

    /// <summary>
    /// For a "waiting" component at a join: how long the person has already been waiting there, in whole seconds,
    /// when the page was built. Null when the engine does not know (a request saved before arrival times were kept).
    /// </summary>
    public int? WaitedSeconds { get; init; }
}
