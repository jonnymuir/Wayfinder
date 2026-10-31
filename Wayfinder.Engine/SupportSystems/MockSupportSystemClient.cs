using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.SupportSystems;

/// <summary>One mocked answer, already checked against the capability it belongs to.</summary>
public sealed record MockSupportSystemAnswer
{
    public required string OutcomeKey { get; init; }

    public JsonObject? Outputs { get; init; }
}

/// <summary>An answer given only when every input in <see cref="When"/> has the given value.</summary>
public sealed record MockSupportSystemCase
{
    public required IReadOnlyDictionary<string, string?> When { get; init; }

    public required MockSupportSystemAnswer Answer { get; init; }
}

/// <summary>Everything a mocked capability can answer: the cases, in order, then the default.</summary>
public sealed record MockSupportSystemCapability
{
    public required MockSupportSystemAnswer Default { get; init; }

    public IReadOnlyList<MockSupportSystemCase> Cases { get; init; } = [];
}

/// <summary>
/// The <see cref="ISupportSystemClient"/> for a support system configured with <c>mock: true</c>.
/// Nothing leaves the host: each invocation picks its answer from the configured cases and
/// resolves on the engine's next check of the waiting join, so the journey carries on at once with
/// the canned outputs merged in. Built by
/// <see cref="Extensions.SupportSystemServiceCollectionExtensions.AddConfiguredSupportSystems"/>.
/// See docs/guides/support-systems.md.
/// </summary>
public sealed partial class MockSupportSystemClient(
    string supportSystemKey,
    IReadOnlyDictionary<string, MockSupportSystemCapability> capabilities,
    ILogger<MockSupportSystemClient> logger) : ISupportSystemClient
{
    // The receipt is persisted with the request, so it carries which answer was picked rather
    // than the client remembering it.
    private const string DefaultAnswer = "default";

    public string SupportSystemKey => supportSystemKey;

    public Task<SupportSystemInvocationReceipt> InvokeAsync(
        string capabilityKey,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct = default)
    {
        var capability = Find(capabilityKey);
        var matched = capability.Cases
            .Select((candidate, index) => (candidate, index))
            .FirstOrDefault(c => Matches(c.candidate.When, inputs));

        var answer = matched.candidate is null ? DefaultAnswer : matched.index.ToString(CultureInfo.InvariantCulture);
        MockInvoked(logger, supportSystemKey, capabilityKey, answer, context.InvocationId);

        return Task.FromResult(new SupportSystemInvocationReceipt { ExternalReference = answer });
    }

    public Task<SupportSystemOutcome?> CheckStatusAsync(
        string capabilityKey,
        SupportSystemInvocationReceipt receipt,
        CancellationToken ct = default)
    {
        var capability = Find(capabilityKey);
        var answer = int.TryParse(receipt.ExternalReference, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                     && index < capability.Cases.Count
            ? capability.Cases[index].Answer
            : capability.Default;

        return Task.FromResult<SupportSystemOutcome?>(new SupportSystemOutcome
        {
            OutcomeKey = answer.OutcomeKey,
            ResultPayload = answer.Outputs?.DeepClone().AsObject(),
        });
    }

    private MockSupportSystemCapability Find(string capabilityKey) =>
        capabilities.TryGetValue(capabilityKey, out var capability)
            ? capability
            : throw new InvalidOperationException(
                $"Mocked support system '{supportSystemKey}' has no capability '{capabilityKey}'.");

    private static bool Matches(
        IReadOnlyDictionary<string, string?> when,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs) =>
        when.All(condition =>
            string.Equals(
                inputs.TryGetValue(condition.Key, out var value) ? AsText(value.RawValue) : null,
                condition.Value,
                StringComparison.OrdinalIgnoreCase));

    private static string? AsText(object? raw) => raw switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        JsonValue value => value.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Mocked support system '{System}' capability '{Capability}' answered with '{Answer}'; invocation {InvocationId}.")]
    private static partial void MockInvoked(ILogger logger, string system, string capability, string answer, string invocationId);
}
