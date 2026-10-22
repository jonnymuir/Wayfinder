using System.ComponentModel;
using ModelContextProtocol.Server;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Mcp;

/// <summary>
/// MCP tools for design notes: the feedback a designer pins to a blueprint on the canvas, and the
/// questions an AI design partner pins back. Served from the same endpoint as
/// <see cref="ServiceBlueprintAuthoringTools"/>, so they carry the same authorization requirement
/// (see <see cref="ServiceBlueprintAuthoringMcpExtensions.MapServiceBlueprintAuthoringMcp"/>). A note an
/// agent writes is always attributed to the design partner, never to a person.
/// </summary>
[McpServerToolType]
public static class DesignNoteTools
{
    private static readonly DesignNoteAuthor DesignPartner = new("Design partner", DesignNoteAuthorKind.DesignPartner);

    private const string AnchorHelp =
        "anchorKind is one of blueprint, stage, gateway, route, field. anchorKey is the stage key, gateway key or " +
        "route id; for a field it is the key of the stage the field is on, with the field's fieldKey in fieldKey. " +
        "A blueprint-level note takes neither.";

    [McpServerTool(Name = "list_design_notes")]
    [Description(
        "List the design notes on a blueprint: feedback the designer has pinned to stages, gateways, routes, fields " +
        "or the whole blueprint, and questions you pinned earlier. Returns open notes unless you ask for another status. " +
        "Each entry has the note (id, anchor, text, author, status, resolution, replies) and anchor: { label, path, " +
        "removed }, where path is where the anchor is in the blueprint JSON now (e.g. stages.check.routes[0]) and " +
        "removed is true when the thing it pointed at has since been deleted. To work through notes: read them, " +
        "change the blueprint, save it, then resolve_design_note each one with what you did, or reply_to_design_note " +
        "if you need to ask before you can. Returns null if the blueprint doesn't exist.")]
    public static Task<IReadOnlyList<DesignNoteListing>?> ListDesignNotes(
        DesignNoteService service,
        [Description("The blueprint's definitionKey.")] string definitionKey,
        [Description("open (the default), resolved, or all.")] string status = "open",
        CancellationToken ct = default)
    {
        DesignNoteStatus? filter = status.ToLowerInvariant() switch
        {
            "all" => null,
            "resolved" => DesignNoteStatus.Resolved,
            _ => DesignNoteStatus.Open,
        };
        return service.ListWithAnchorsAsync(definitionKey, filter, ct);
    }

    [McpServerTool(Name = "add_design_note")]
    [Description(
        "Pin a note to part of a blueprint, for the designer to see on the canvas. Use it to ask a design question " +
        "where its context is, instead of in the chat (\"Should an expired licence be refused here, or sent to a " +
        "caseworker?\"), or to flag something you noticed but weren't asked to change. " + AnchorHelp + " " +
        "Returns { status, note, error }; status is Ok, NotFound or Invalid.")]
    public static Task<DesignNoteOutcome> AddDesignNote(
        DesignNoteService service,
        [Description("The blueprint's definitionKey.")] string definitionKey,
        [Description("blueprint, stage, gateway, route or field.")] DesignNoteAnchorKind anchorKind,
        [Description("What the note says.")] string text,
        [Description("The stage key, gateway key, route id, or (for a field) the stage key. Omit for a blueprint-level note.")] string? anchorKey = null,
        [Description("For a field anchor only: the field's fieldKey.")] string? fieldKey = null,
        CancellationToken ct = default) =>
        service.AddAsync(definitionKey, new DesignNoteAnchor(anchorKind, anchorKey, fieldKey), text, DesignPartner, ct);

    [McpServerTool(Name = "resolve_design_note")]
    [Description(
        "Mark a design note resolved, with one sentence on what you did. Save the blueprint first, then pass the " +
        "version the save returned, so the designer can see which version addressed the note. Only resolve a note " +
        "you actually addressed; if you need the designer's input first, use reply_to_design_note instead. Returns " +
        "{ status, note, error }.")]
    public static Task<DesignNoteOutcome> ResolveDesignNote(
        DesignNoteService service,
        [Description("The blueprint's definitionKey.")] string definitionKey,
        [Description("The note's id, from list_design_notes.")] string noteId,
        [Description("What you did, in a sentence.")] string summary,
        [Description("The blueprint version your change was saved as. Defaults to the current version.")] int? version = null,
        CancellationToken ct = default) =>
        service.ResolveAsync(definitionKey, noteId, summary, DesignPartner, version, ct);

    [McpServerTool(Name = "reply_to_design_note")]
    [Description(
        "Reply to a design note, for when you need to ask or explain before it can be resolved. The note stays " +
        "open. Returns { status, note, error }.")]
    public static Task<DesignNoteOutcome> ReplyToDesignNote(
        DesignNoteService service,
        [Description("The blueprint's definitionKey.")] string definitionKey,
        [Description("The note's id, from list_design_notes.")] string noteId,
        [Description("Your reply.")] string text,
        CancellationToken ct = default) =>
        service.ReplyAsync(definitionKey, noteId, text, DesignPartner, ct);
}
