using System.Text.Json.Serialization;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

[JsonConverter(typeof(JsonStringEnumConverter<DesignNoteOutcomeStatus>))]
public enum DesignNoteOutcomeStatus { Ok, NotFound, Invalid, Conflict }

/// <summary>The result of changing a note: the note as stored now, or why it wasn't changed.</summary>
public sealed record DesignNoteOutcome(DesignNoteOutcomeStatus Status, DesignNote? Note = null, string? Error = null)
{
    internal static DesignNoteOutcome Ok(DesignNote note) => new(DesignNoteOutcomeStatus.Ok, note);

    internal static DesignNoteOutcome NotFound(string error) => new(DesignNoteOutcomeStatus.NotFound, Error: error);

    internal static DesignNoteOutcome Invalid(string error) => new(DesignNoteOutcomeStatus.Invalid, Error: error);
}

/// <summary>A note with its anchor resolved against the blueprint as it is now.</summary>
public sealed record DesignNoteListing(DesignNote Note, ResolvedDesignNoteAnchor Anchor);

/// <summary>
/// Adding, answering and resolving design notes, for any front door: the MCP tools an AI design partner
/// uses, the REST routes the editor uses, or a host's own code. Every operation checks the blueprint
/// exists, and a new note's anchor must point at something in it.
/// </summary>
public sealed class DesignNoteService(IServiceBlueprintSourceStore blueprints, IDesignNoteStore notes, TimeProvider? time = null)
{
    public const int MaxTextLength = 4000;
    public const int MaxNotesPerBlueprint = 500;
    public const int MaxRepliesPerNote = 100;
    private const int MaxAuthorNameLength = 200;
    private const int UpdateAttempts = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Notes on a blueprint, oldest first; null if the blueprint doesn't exist.</summary>
    public async Task<IReadOnlyList<DesignNote>?> ListAsync(string blueprintKey, DesignNoteStatus? status = null, CancellationToken ct = default)
    {
        if (await blueprints.LoadAsync(blueprintKey, ct) is null)
        {
            return null;
        }
        var all = await notes.ListAsync(blueprintKey, ct);
        return status is null ? all : all.Where(n => n.Status == status).ToArray();
    }

    /// <summary>As <see cref="ListAsync"/>, with each anchor resolved against the blueprint's current version.</summary>
    public async Task<IReadOnlyList<DesignNoteListing>?> ListWithAnchorsAsync(
        string blueprintKey, DesignNoteStatus? status = null, CancellationToken ct = default)
    {
        var blueprint = await blueprints.LoadAsync(blueprintKey, ct);
        if (blueprint is null)
        {
            return null;
        }
        var all = await notes.ListAsync(blueprintKey, ct);
        return all.Where(n => status is null || n.Status == status)
            .Select(n => new DesignNoteListing(n, DesignNoteAnchors.Resolve(blueprint, n.Anchor)))
            .ToArray();
    }

    public async Task<DesignNoteOutcome> AddAsync(
        string blueprintKey, DesignNoteAnchor anchor, string text, DesignNoteAuthor author, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var blueprint = await blueprints.LoadAsync(blueprintKey, ct);
        if (blueprint is null)
        {
            return BlueprintNotFound(blueprintKey);
        }
        if ((TextProblem(text, "text") ?? AuthorProblem(author) ?? DesignNoteAnchors.Problem(blueprint, anchor)) is { } problem)
        {
            return DesignNoteOutcome.Invalid(problem);
        }
        if ((await notes.ListAsync(blueprintKey, ct)).Count >= MaxNotesPerBlueprint)
        {
            return DesignNoteOutcome.Invalid($"A blueprint can have at most {MaxNotesPerBlueprint} notes. Delete some resolved ones first.");
        }

        var note = new DesignNote
        {
            Id = Guid.NewGuid().ToString("N"),
            BlueprintKey = blueprintKey,
            Anchor = anchor,
            Text = text.Trim(),
            Author = Tidy(author),
            CreatedAt = _time.GetUtcNow(),
        };
        await notes.AddAsync(note, ct);
        return DesignNoteOutcome.Ok(note);
    }

    public Task<DesignNoteOutcome> ReplyAsync(
        string blueprintKey, string noteId, string text, DesignNoteAuthor author, CancellationToken ct = default) =>
        ChangeAsync(blueprintKey, noteId, note => ReplyProblem(note, text, author), (note, _) => Replied(note, text, author), ct);

    /// <summary>
    /// Marks a note resolved with what was done. <paramref name="version"/> is the blueprint version
    /// the change landed in; it defaults to the current version.
    /// </summary>
    public Task<DesignNoteOutcome> ResolveAsync(
        string blueprintKey, string noteId, string summary, DesignNoteAuthor author, int? version = null, CancellationToken ct = default) =>
        ChangeAsync(blueprintKey, noteId, note => ResolveProblem(note, summary, author),
            (note, blueprint) => Resolved(note, summary, author, Math.Clamp(version ?? blueprint.Version, 0, blueprint.Version)), ct);

    /// <summary>
    /// Opens a resolved note again. Its resolution becomes a reply, so the thread still shows what was
    /// tried, followed by <paramref name="reason"/> if there is one.
    /// </summary>
    public Task<DesignNoteOutcome> ReopenAsync(
        string blueprintKey, string noteId, DesignNoteAuthor author, string? reason = null, CancellationToken ct = default) =>
        ChangeAsync(blueprintKey, noteId, note => ReopenProblem(note, author, reason), (note, _) => Reopened(note, author, reason), ct);

    private static string? ReplyProblem(DesignNote note, string text, DesignNoteAuthor author) =>
        TextProblem(text, "text") ?? AuthorProblem(author)
        ?? (note.Replies.Count >= MaxRepliesPerNote ? $"A note can have at most {MaxRepliesPerNote} replies." : null);

    private static string? ResolveProblem(DesignNote note, string summary, DesignNoteAuthor author) =>
        note.Status == DesignNoteStatus.Resolved ? "This note is already resolved." : TextProblem(summary, "summary") ?? AuthorProblem(author);

    private static string? ReopenProblem(DesignNote note, DesignNoteAuthor author, string? reason) =>
        note.Status == DesignNoteStatus.Open ? "This note is already open."
        : (string.IsNullOrWhiteSpace(reason) ? null : TextProblem(reason, "reason")) ?? AuthorProblem(author);

    private DesignNote Replied(DesignNote note, string text, DesignNoteAuthor author) =>
        note with { Replies = [.. note.Replies, new(Tidy(author), text.Trim(), _time.GetUtcNow())] };

    private DesignNote Resolved(DesignNote note, string summary, DesignNoteAuthor author, int version) =>
        note with { Status = DesignNoteStatus.Resolved, Resolution = new(Tidy(author), summary.Trim(), version, _time.GetUtcNow()) };

    private DesignNote Reopened(DesignNote note, DesignNoteAuthor author, string? reason)
    {
        var resolution = note.Resolution!;
        var replies = note.Replies.Append(new(resolution.Author, $"Resolved in version {resolution.Version}: {resolution.Summary}", resolution.ResolvedAt));
        if (!string.IsNullOrWhiteSpace(reason))
        {
            replies = replies.Append(new(Tidy(author), reason.Trim(), _time.GetUtcNow()));
        }
        return note with { Status = DesignNoteStatus.Open, Resolution = null, Replies = [.. replies] };
    }

    public async Task<bool> DeleteAsync(string blueprintKey, string noteId, CancellationToken ct = default) =>
        await notes.DeleteAsync(blueprintKey, noteId, ct);

    private async Task<DesignNoteOutcome> ChangeAsync(
        string blueprintKey,
        string noteId,
        Func<DesignNote, string?> problem,
        Func<DesignNote, ServiceBlueprint, DesignNote> change,
        CancellationToken ct)
    {
        var blueprint = await blueprints.LoadAsync(blueprintKey, ct);
        if (blueprint is null)
        {
            return BlueprintNotFound(blueprintKey);
        }
        // Two people replying at once both land: a lost compare-and-write rereads and reapplies.
        for (var attempt = 0; attempt < UpdateAttempts; attempt++)
        {
            var note = await notes.GetAsync(blueprintKey, noteId, ct);
            if (note is null)
            {
                return DesignNoteOutcome.NotFound($"There is no note '{noteId}' on blueprint '{blueprintKey}'.");
            }
            if (problem(note) is { } error)
            {
                return DesignNoteOutcome.Invalid(error);
            }
            var updated = change(note, blueprint);
            if (await notes.UpdateAsync(updated, note.Revision, ct))
            {
                return DesignNoteOutcome.Ok(updated with { Revision = note.Revision + 1 });
            }
        }
        return new(DesignNoteOutcomeStatus.Conflict, Error: "The note kept changing while this was being saved. Try again.");
    }

    private static DesignNoteOutcome BlueprintNotFound(string blueprintKey) =>
        DesignNoteOutcome.NotFound($"There is no blueprint '{blueprintKey}'.");

    private static string? TextProblem(string? text, string name) =>
        string.IsNullOrWhiteSpace(text) ? $"The {name} can't be empty."
        : text.Trim().Length > MaxTextLength ? $"The {name} can be at most {MaxTextLength} characters."
        : null;

    private static string? AuthorProblem(DesignNoteAuthor? author) =>
        author is null || string.IsNullOrWhiteSpace(author.Name) ? "A note needs an author."
        : author.Name.Trim().Length > MaxAuthorNameLength ? $"An author name can be at most {MaxAuthorNameLength} characters."
        : null;

    private static DesignNoteAuthor Tidy(DesignNoteAuthor author) => author with { Name = author.Name.Trim() };
}
