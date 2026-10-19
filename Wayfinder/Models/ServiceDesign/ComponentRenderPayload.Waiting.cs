namespace Wayfinder.Models.ServiceDesign;

public partial record ComponentRenderPayload
{
    /// <summary>
    /// For a "waiting" component at a join: the display names of the queues it is still waiting on, kept
    /// apart from <see cref="Content"/> so a host decides whether and how to say who it is waiting for.
    /// </summary>
    public IReadOnlyList<string>? WaitingFor { get; init; }
}
