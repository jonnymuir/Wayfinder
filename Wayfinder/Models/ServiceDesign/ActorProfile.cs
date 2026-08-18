namespace Wayfinder.Models.ServiceDesign;

public record ActorProfile
{
    public static ActorProfile UnrestrictedOwner { get; } = new();

    public IReadOnlyList<string> VisibleQueues { get; init; } = [];

    public IReadOnlyList<string> StartableQueues { get; init; } = [];

    public IReadOnlyList<string> ActionableQueues { get; init; } = [];

    public bool RestrictToInstanceOwner { get; init; } = true;

    /// <summary>
    /// When set, overrides <c>userId</c> as the key <c>ProcessManagerEngine.FindLatestInstance</c>
    /// groups "is there already one?" by, for the "single"/"prompt" request policies. Null (the
    /// default) reproduces today's exact per-user behaviour. A host sets this to group instances
    /// by something other than the individual requester — e.g. one organisation's several users
    /// all sharing one in-flight bulk submission — resolved the same way the host already resolves
    /// tenantId/userId themselves (a claim, a lookup, static config), not from blueprint-declared
    /// field values: at the point this decision is made, a brand-new instance's FieldValues is
    /// still empty, so there'd be nothing to resolve a field-ref against yet.
    /// </summary>
    public string? ConcurrencyScopeKey { get; init; }

    public bool CanViewQueue(string? queueName) => IsAllowed(queueName, VisibleQueues);

    public bool CanStartQueue(string? queueName) => IsAllowed(queueName, StartableQueues);

    public bool CanActInQueue(string? queueName) => IsAllowed(queueName, ActionableQueues);

    private static bool IsAllowed(string? queueName, IReadOnlyList<string> allowedQueues)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return true;
        }

        if (allowedQueues.Count == 0)
        {
            return true;
        }

        return allowedQueues.Any(candidate =>
            string.Equals(candidate, queueName, StringComparison.OrdinalIgnoreCase));
    }
}
