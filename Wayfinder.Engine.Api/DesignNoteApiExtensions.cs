using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Api;

/// <summary>
/// The editor's design-note routes, over <see cref="DesignNoteService"/>. Mapped only inside
/// <see cref="ServiceBlueprintAuthoringApiExtensions.MapServiceBlueprintAuthoringApi"/>'s group, so they carry
/// exactly its authorization requirement: whoever may edit a blueprint may read and write its notes.
/// Notes written here are attributed to the signed-in person.
/// </summary>
internal static class DesignNoteApiExtensions
{
    public static void MapDesignNotes(this RouteGroupBuilder group)
    {
        group.MapGet("/blueprints/{definitionKey}/notes", async (string definitionKey, DesignNoteService service, CancellationToken ct) =>
            await service.ListAsync(definitionKey, ct: ct) is { } notes ? Results.Ok(notes) : Results.NotFound());

        group.MapPost("/blueprints/{definitionKey}/notes", async (
            string definitionKey, AddDesignNoteRequest request, ClaimsPrincipal user, DesignNoteService service, CancellationToken ct) =>
            ToResult(await service.AddAsync(definitionKey, request.Anchor, request.Text, Author(user), ct)));

        group.MapPost("/blueprints/{definitionKey}/notes/{noteId}/replies", async (
            string definitionKey, string noteId, DesignNoteTextRequest request, ClaimsPrincipal user, DesignNoteService service, CancellationToken ct) =>
            ToResult(await service.ReplyAsync(definitionKey, noteId, request.Text, Author(user), ct)));

        group.MapPost("/blueprints/{definitionKey}/notes/{noteId}/resolve", async (
            string definitionKey, string noteId, ResolveDesignNoteRequest request, ClaimsPrincipal user, DesignNoteService service, CancellationToken ct) =>
            ToResult(await service.ResolveAsync(definitionKey, noteId, request.Summary, Author(user), request.Version, ct)));

        group.MapPost("/blueprints/{definitionKey}/notes/{noteId}/reopen", async (
            string definitionKey, string noteId, ReopenDesignNoteRequest request, ClaimsPrincipal user, DesignNoteService service, CancellationToken ct) =>
            ToResult(await service.ReopenAsync(definitionKey, noteId, Author(user), request.Reason, ct)));

        group.MapDelete("/blueprints/{definitionKey}/notes/{noteId}", async (
            string definitionKey, string noteId, DesignNoteService service, CancellationToken ct) =>
            await service.DeleteAsync(definitionKey, noteId, ct) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>A host that maps these routes anonymously (a local demo) has nobody to name, so the note says "Designer".</summary>
    private static DesignNoteAuthor Author(ClaimsPrincipal user) =>
        new(user.Identity?.Name ?? user.FindFirst("name")?.Value ?? "Designer", DesignNoteAuthorKind.Person);

    private static IResult ToResult(DesignNoteOutcome outcome) => outcome.Status switch
    {
        DesignNoteOutcomeStatus.Ok => Results.Ok(outcome.Note),
        DesignNoteOutcomeStatus.NotFound => Results.NotFound(outcome),
        DesignNoteOutcomeStatus.Conflict => Results.Conflict(outcome),
        _ => Results.BadRequest(outcome),
    };
}
