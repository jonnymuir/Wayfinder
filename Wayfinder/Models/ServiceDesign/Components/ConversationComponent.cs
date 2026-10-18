namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// A running conversation between the person a request belongs to and the team handling it. The
/// field keeps every message in order, with which queue wrote it and when; the control is a box for
/// the next message, which is added to the end when the stage is submitted rather than replacing
/// what came before. Put the same <see cref="InputComponent.FieldKey"/> on a stage in each queue that
/// takes part, and they all write to one thread.
/// </summary>
public sealed record ConversationComponent : InputComponent
{
    /// <summary>
    /// The longest one message may be. Null falls back to <see cref="DefaultMaxLength"/>: a thread only
    /// ever grows, so every message is bounded.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>The limit applied to a message when <see cref="MaxLength"/> is not set.</summary>
    public const int DefaultMaxLength = 5000;
}
