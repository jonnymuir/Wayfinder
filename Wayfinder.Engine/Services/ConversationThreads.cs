using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Extensions;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>
/// What a submission means for a stage's conversation fields: the box holds the next message, never
/// the thread, so an accepted submission adds that message to the end of the stored thread instead of
/// replacing it, and an empty box leaves the thread exactly as it was.
/// </summary>
internal static class ConversationThreads
{
    /// <summary>The field keys of the conversation fields on <paramref name="stage"/>.</summary>
    public static IReadOnlySet<string> FieldKeys(StageDefinition stage) =>
        Conversations(stage).Select(c => c.FieldKey).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The submitted values with each conversation field's message turned into the whole thread plus that
    /// message, written by <paramref name="stage"/>'s queue, at <paramref name="at"/>. An empty message is
    /// dropped, so the thread is not overwritten.
    /// </summary>
    public static Dictionary<string, object?>? AppendSubmitted(
        StageDefinition stage,
        IReadOnlyDictionary<string, object?> stored,
        Dictionary<string, object?>? submitted,
        DateTimeOffset at)
    {
        var conversations = Conversations(stage).ToArray();
        if (submitted is null || conversations.Length == 0)
        {
            return submitted;
        }

        var result = new Dictionary<string, object?>(submitted, StringComparer.Ordinal);
        foreach (var conversation in conversations)
        {
            if (!result.TryGetValue(conversation.FieldKey, out var message))
            {
                continue;
            }

            var text = message?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Remove(conversation.FieldKey);
                continue;
            }

            var thread = ConversationEntry.ReadThread(stored.GetValueOrDefault(conversation.FieldKey));
            result[conversation.FieldKey] = thread
                .Append(new ConversationEntry { Text = text, QueueKey = GetQueueKey(stage) ?? "", At = at })
                .ToArray();
        }

        return result;
    }

    /// <summary>
    /// The submitted values without the conversation fields' unsent messages, for re-rendering a rejected
    /// submission: the stored thread must still show, and nothing about it has changed.
    /// </summary>
    public static Dictionary<string, object?>? WithoutMessages(StageDefinition stage, Dictionary<string, object?>? submitted)
    {
        var keys = FieldKeys(stage);
        return submitted is null || keys.Count == 0
            ? submitted
            : submitted.Where(kvp => !keys.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    /// <summary>The longest one message on a conversation field may be.</summary>
    public static int MaxLength(StageDefinition stage, string fieldKey) =>
        Conversations(stage).FirstOrDefault(c => string.Equals(c.FieldKey, fieldKey, StringComparison.Ordinal))?.MaxLength
        ?? ConversationComponent.DefaultMaxLength;

    private static IEnumerable<ConversationComponent> Conversations(StageDefinition stage) =>
        stage.Components.GetAllInputs().OfType<ConversationComponent>();
}
