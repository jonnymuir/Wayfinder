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

    /// <summary>
    /// Host-resolved skill/team tags this actor holds — same resolution pattern as
    /// <see cref="ConcurrencyScopeKey"/> (a claim, a lookup, static config), matched
    /// case-insensitively. Checked against a queue's own declared <c>QueueDefinition.RoleGates</c>
    /// (see <see cref="HasCapability"/>) — distinct from <c>IQueueCapabilitiesProvider</c>'s
    /// unrelated, pre-existing use of the word "capability" for which component types a host can
    /// render; see docs/guides/work-allocation.md.
    /// </summary>
    public IReadOnlySet<string> Capabilities { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool CanViewQueue(string? queueName) => IsAllowed(queueName, VisibleQueues);

    public bool CanStartQueue(string? queueName) => IsAllowed(queueName, StartableQueues);

    public bool CanActInQueue(string? queueName) => IsAllowed(queueName, ActionableQueues);

    /// <summary>
    /// True when <paramref name="requiredCapabilities"/> is null/empty (the queue declares no
    /// restriction — every blueprint that predates this) or this profile holds at least one of the
    /// listed capabilities (any-of: more than one team can be eligible for the same queue).
    ///
    /// A profile that imposes no restriction on any of the three queue-name allow-lists
    /// (<see cref="VisibleQueues"/>/<see cref="StartableQueues"/>/<see cref="ActionableQueues"/> all
    /// empty — <see cref="UnrestrictedOwner"/>'s own shape, and the implicit default for every
    /// overload of <c>GetCurrent</c>/<c>Advance</c> that takes no explicit profile) is also
    /// unrestricted here, deliberately: those call sites must keep working unchanged once a real
    /// blueprint's queue starts declaring <c>RoleGates</c>, the same way they already aren't
    /// restricted by queue name today.
    /// </summary>
    public bool HasCapability(IReadOnlyList<string>? requiredCapabilities)
    {
        if (requiredCapabilities is null || requiredCapabilities.Count == 0)
        {
            return true;
        }

        if (VisibleQueues.Count == 0 && StartableQueues.Count == 0 && ActionableQueues.Count == 0)
        {
            return true;
        }

        return requiredCapabilities.Any(required => Capabilities.Contains(required));
    }

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
