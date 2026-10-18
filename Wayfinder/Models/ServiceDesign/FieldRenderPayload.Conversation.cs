namespace Wayfinder.Models.ServiceDesign;

public partial record FieldRenderPayload
{
    /// <summary>
    /// A conversation field's earlier messages, oldest first, labelled for whoever the stage is being
    /// rendered for. Null for every other field type. <see cref="Value"/> is the next message, which
    /// starts empty.
    /// </summary>
    public IReadOnlyList<ConversationMessagePayload>? Conversation { get; init; }
}

/// <summary>One earlier message in a conversation field, ready to display.</summary>
public sealed record ConversationMessagePayload
{
    public string Text { get; init; } = "";

    public DateTimeOffset At { get; init; }

    /// <summary>"You" for the viewer's own side, otherwise the writing queue's display name.</summary>
    public string AuthorLabel { get; init; } = "";

    /// <summary>Whether the viewer's side of the conversation wrote it.</summary>
    public bool IsOwnSide { get; init; }
}
