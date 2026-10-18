using System.Text.Json;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>One message in a <see cref="Components.ConversationComponent"/>'s thread.</summary>
public sealed record ConversationEntry
{
    /// <summary>What was written, exactly as submitted.</summary>
    public string Text { get; init; } = "";

    /// <summary>The queue of the stage it was written on: whose side of the conversation it is.</summary>
    public string QueueKey { get; init; } = "";

    /// <summary>When it was submitted.</summary>
    public DateTimeOffset At { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads a stored thread back, whichever shape it arrives in: the list the engine saved, or the JSON
    /// a store hands back after a round trip. Anything else (nothing yet, or a value that isn't a
    /// thread) reads as an empty thread rather than failing the page.
    /// </summary>
    public static IReadOnlyList<ConversationEntry> ReadThread(object? value) => value switch
    {
        IReadOnlyList<ConversationEntry> entries => entries,
        IEnumerable<ConversationEntry> entries => entries.ToArray(),
        JsonElement { ValueKind: JsonValueKind.Array } json => json.Deserialize<ConversationEntry[]>(JsonOptions) ?? [],
        _ => [],
    };
}
