namespace Wayfinder.Models.ServiceDesign.SupportSystems;

/// <summary>
/// What a <c>support-system-call</c> action does when the call cannot be dispatched (the support
/// system is unreachable or answers with an error status). Read from the action's own
/// <c>params</c>; every setting is opt-in, so an action with none of them fails fast:
/// the visitor stays on the stage with a "try again" message and nothing is saved.
/// <list type="bullet">
/// <item><c>retries</c> (0–<see cref="MaxRetries"/>, default 0): extra attempts before giving up.</item>
/// <item><c>retryDelaySeconds</c> (1–<see cref="MaxRetryDelaySeconds"/>, default 2): wait before the first retry; doubles each retry.</item>
/// <item><c>onFailure</c> <c>{ outcome, outputs }</c>: instead of failing, resolve the call
/// as the capability's declared <c>outcome</c> (with literal <c>outputs</c> merged into the
/// instance), so the journey carries on down its normal route for that outcome.</item>
/// </list>
/// Retries re-send the same call, so use them only against an idempotent consumer.
/// </summary>
public sealed record SupportCallFailurePolicy
{
    public int Retries { get; init; }

    public TimeSpan InitialRetryDelay { get; init; }

    public string? FallbackOutcome { get; init; }

    public IReadOnlyDictionary<string, object?>? FallbackOutputs { get; init; }

    public const int MaxRetries = 5;

    public const int MaxRetryDelaySeconds = 30;

    private const int DefaultRetryDelaySeconds = 2;

    public static SupportCallFailurePolicy From(IReadOnlyDictionary<string, object?> parameters)
    {
        var retries = Math.Clamp(parameters.GetInt("retries") ?? 0, 0, MaxRetries);
        var delaySeconds = Math.Clamp(parameters.GetInt("retryDelaySeconds") ?? DefaultRetryDelaySeconds, 1, MaxRetryDelaySeconds);
        var onFailure = parameters.GetMap("onFailure");
        return new SupportCallFailurePolicy
        {
            Retries = retries,
            InitialRetryDelay = TimeSpan.FromSeconds(delaySeconds),
            FallbackOutcome = onFailure?.GetString("outcome"),
            FallbackOutputs = onFailure?.GetMap("outputs"),
        };
    }

    /// <summary>Authoring diagnostics for the failure settings of one action; <paramref name="path"/> is the action's path.</summary>
    public static IEnumerable<ServiceBlueprintDiagnostic> Validate(
        IReadOnlyDictionary<string, object?> parameters, string path, IReadOnlySet<string> declaredOutcomeKeys)
    {
        foreach (var (key, min, max) in new[] { ("retries", 0, MaxRetries), ("retryDelaySeconds", 1, MaxRetryDelaySeconds) })
        {
            if (parameters.GetValueOrDefault(key) is not null && !InRange(parameters.GetInt(key), min, max))
            {
                yield return Invalid($"{path}.params.{key}", $"params.{key} must be a whole number from {min} to {max}.");
            }
        }

        if (parameters.GetValueOrDefault("onFailure") is not null
            && !(parameters.GetMap("onFailure")?.GetString("outcome") is { } outcome && declaredOutcomeKeys.Contains(outcome)))
        {
            yield return Invalid(
                $"{path}.params.onFailure.outcome",
                $"params.onFailure.outcome must be one of the capability's declared outcomes ({string.Join(", ", declaredOutcomeKeys)}).");
        }
    }

    private static ServiceBlueprintDiagnostic Invalid(string path, string message) =>
        new("SUPPORT_SYSTEM_ACTION_INVALID_FAILURE_POLICY", path, message);

    private static bool InRange(int? number, int min, int max) => number >= min && number <= max;
}
