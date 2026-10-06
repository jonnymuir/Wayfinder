using System.Text.Json.Nodes;

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

    public JsonObject? FallbackOutputs { get; init; }

    public const int MaxRetries = 5;

    public const int MaxRetryDelaySeconds = 30;

    private const int DefaultRetryDelaySeconds = 2;

    public static SupportCallFailurePolicy From(JsonObject parameters)
    {
        var retries = Math.Clamp(ReadInt(parameters, "retries") ?? 0, 0, MaxRetries);
        var delaySeconds = Math.Clamp(ReadInt(parameters, "retryDelaySeconds") ?? DefaultRetryDelaySeconds, 1, MaxRetryDelaySeconds);
        var onFailure = parameters["onFailure"] as JsonObject;
        return new SupportCallFailurePolicy
        {
            Retries = retries,
            InitialRetryDelay = TimeSpan.FromSeconds(delaySeconds),
            FallbackOutcome = onFailure?["outcome"]?.GetValue<string>(),
            FallbackOutputs = onFailure?["outputs"] as JsonObject,
        };
    }

    /// <summary>Authoring diagnostics for the failure settings of one action; <paramref name="path"/> is the action's path.</summary>
    public static IEnumerable<ServiceBlueprintDiagnostic> Validate(
        JsonObject parameters, string path, IReadOnlySet<string> declaredOutcomeKeys)
    {
        foreach (var (key, min, max) in new[] { ("retries", 0, MaxRetries), ("retryDelaySeconds", 1, MaxRetryDelaySeconds) })
        {
            if (parameters[key] is { } value && !InRange(value, min, max))
            {
                yield return Invalid($"{path}.params.{key}", $"params.{key} must be a whole number from {min} to {max}.");
            }
        }

        if (parameters["onFailure"] is { } onFailure && !IsDeclaredOutcome(onFailure, declaredOutcomeKeys))
        {
            yield return Invalid(
                $"{path}.params.onFailure.outcome",
                $"params.onFailure.outcome must be one of the capability's declared outcomes ({string.Join(", ", declaredOutcomeKeys)}).");
        }
    }

    private static bool IsDeclaredOutcome(JsonNode onFailure, IReadOnlySet<string> declaredOutcomeKeys) =>
        (onFailure as JsonObject)?["outcome"] is JsonValue value
        && value.TryGetValue<string>(out var outcome)
        && declaredOutcomeKeys.Contains(outcome);

    private static ServiceBlueprintDiagnostic Invalid(string path, string message) =>
        new("SUPPORT_SYSTEM_ACTION_INVALID_FAILURE_POLICY", path, message);

    private static int? ReadInt(JsonObject parameters, string key) =>
        parameters[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool InRange(JsonNode node, int min, int max) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number >= min && number <= max;
}
