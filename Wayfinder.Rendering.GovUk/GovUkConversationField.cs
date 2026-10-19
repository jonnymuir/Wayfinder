using System.Globalization;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

internal static class GovUkConversationField
{
    /// <summary>
    /// The conversation so far as an ordered list, oldest first, each message headed with who wrote it
    /// and when, then a GOV.UK textarea for the next message. GOV.UK has no conversation component, so the
    /// thread uses plain GOV.UK typography; the box is the standard textarea. Every message is escaped,
    /// with its line breaks kept.
    /// </summary>
    internal static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var messages = field.Conversation ?? [];
        var thread = messages.Count == 0
            ? ""
            : $"""
              <ol class="govuk-list wayfinder-conversation" aria-label="{GovUk.Esc(field.Label)}: conversation so far">
                {string.Concat(messages.Select(Message))}
              </ol>
              """;
        return thread + GovUkFields.RenderTextarea(field, errors);
    }

    private static string Message(ConversationMessagePayload message)
    {
        var sideClass = message.IsOwnSide ? " wayfinder-conversation__message--own" : "";
        var paragraphs = string.Concat(message.Text
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => $"""<p class="govuk-body">{GovUk.Esc(paragraph).ReplaceLineEndings("<br>")}</p>"""));
        // Shown in whatever offset the time carries: a host converts to the time zone its visitors read.
        var at = message.At;
        return $"""
            <li class="wayfinder-conversation__message{sideClass}">
              <h3 class="govuk-heading-s govuk-!-margin-bottom-1">{GovUk.Esc(message.AuthorLabel)}</h3>
              <p class="govuk-body-s govuk-!-margin-bottom-2"><time datetime="{at.ToString("O", CultureInfo.InvariantCulture)}">{at.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}</time></p>
              {paragraphs}
            </li>
            """;
    }
}
