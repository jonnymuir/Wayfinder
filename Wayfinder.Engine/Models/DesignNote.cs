using System.Text.Json.Serialization;

namespace Wayfinder.Engine.Models;

/// <summary>What a design note is pinned to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DesignNoteAnchorKind>))]
public enum DesignNoteAnchorKind { Blueprint, Stage, Gateway, Route, Field }

[JsonConverter(typeof(JsonStringEnumConverter<DesignNoteStatus>))]
public enum DesignNoteStatus { Open, Resolved }

/// <summary>A person, or an AI design partner working through the authoring tools.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DesignNoteAuthorKind>))]
public enum DesignNoteAuthorKind { Person, DesignPartner }

/// <summary>
/// Where a note points. <see cref="Key"/> is the stage key, gateway key or route id, by
/// <see cref="Kind"/>; for a field it is the stage the field is on, and <see cref="FieldKey"/> names
/// the field. A blueprint-level note has neither.
/// </summary>
public sealed record DesignNoteAnchor(DesignNoteAnchorKind Kind, string? Key = null, string? FieldKey = null);

public sealed record DesignNoteAuthor(string Name, DesignNoteAuthorKind Kind);

public sealed record DesignNoteReply(DesignNoteAuthor Author, string Text, DateTimeOffset CreatedAt);

/// <summary>What was done about a note, and the blueprint version it was done in.</summary>
public sealed record DesignNoteResolution(DesignNoteAuthor Author, string Summary, int Version, DateTimeOffset ResolvedAt);

/// <summary>
/// Feedback pinned to part of a blueprint, by a designer or by an AI design partner. Notes are about
/// the design, not part of it: they live beside the blueprint, never inside its JSON, so adding or
/// resolving one never changes the blueprint's version.
/// </summary>
public sealed record DesignNote
{
    public required string Id { get; init; }

    public required string BlueprintKey { get; init; }

    public required DesignNoteAnchor Anchor { get; init; }

    public required string Text { get; init; }

    public required DesignNoteAuthor Author { get; init; }

    public DesignNoteStatus Status { get; init; } = DesignNoteStatus.Open;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DesignNoteResolution? Resolution { get; init; }

    public IReadOnlyList<DesignNoteReply> Replies { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set by the store on every write, for its compare-and-write. Starts at 1.</summary>
    public int Revision { get; init; } = 1;
}
