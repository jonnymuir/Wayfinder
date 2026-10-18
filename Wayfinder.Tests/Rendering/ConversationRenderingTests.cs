using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// A conversation field renders the thread so far, oldest first, with who wrote each message and
/// when, and a GOV.UK textarea for the next one. Message text is never trusted as markup.
/// </summary>
public class ConversationRenderingTests
{
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    private static FieldRenderPayload Field(params ConversationMessagePayload[] messages) => new()
    {
        FieldKey = "thread",
        Label = "Your message",
        FieldType = "conversation",
        Required = false,
        Value = "",
        MaxLength = 500,
        Conversation = messages,
    };

    private static ConversationMessagePayload Msg(string author, string text, bool own, string at = "2026-10-18T14:05:00Z") => new()
    {
        AuthorLabel = author, Text = text, IsOwnSide = own, At = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public void TheThread_RendersOldestFirst_WithWhoWroteEachMessageAndWhen()
    {
        var html = GovUkFields.Render(Field(
            Msg("You", "Are you open on Sunday?", own: true, at: "2026-10-18T09:30:00Z"),
            Msg("The Exchange", "Yes, 10am to 4pm.", own: false, at: "2026-10-18T14:05:00Z")), NoErrors);

        html.IndexOf("Are you open on Sunday?", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Yes, 10am to 4pm.", StringComparison.Ordinal));
        html.Should().Contain(">You</h3>").And.Contain(">The Exchange</h3>");
        html.Should().Contain("datetime=\"2026-10-18T14:05:00.0000000+00:00\"").And.Contain("18 October 2026, 14:05 UTC");
        html.Should().Contain("<ol class=\"govuk-list wayfinder-conversation\"");
    }

    [Fact]
    public void TheNextMessage_IsAGovUkTextarea_UnderTheFieldsLabel_WithItsLimit()
    {
        var html = GovUkFields.Render(Field(Msg("You", "Hello", own: true)), NoErrors);

        html.Should().Contain("<label class=\"govuk-label\" for=\"thread\">Your message</label>");
        html.Should().Contain("<textarea class=\"govuk-textarea\" id=\"thread\" name=\"field:thread\"");
        html.Should().Contain("maxlength=\"500\"");
    }

    [Fact]
    public void AConversationWithNoMessagesYet_RendersOnlyTheBox()
    {
        var html = GovUkFields.Render(Field(), NoErrors);

        html.Should().NotContain("wayfinder-conversation");
        html.Should().Contain("<textarea");
    }

    [Fact]
    public void AMessage_KeepsItsParagraphsAndLineBreaks()
    {
        var html = GovUkFields.Render(Field(Msg("You", "Line one\nLine two\n\nNew paragraph", own: true)), NoErrors);

        html.Should().Contain("<p class=\"govuk-body\">Line one<br>Line two</p>");
        html.Should().Contain("<p class=\"govuk-body\">New paragraph</p>");
    }

    [Fact]
    public void MarkupInAMessageOrAnAuthorLabel_IsEscaped_NeverRendered()
    {
        var html = GovUkFields.Render(Field(
            Msg("<img src=x onerror=alert(1)>", "<script>alert('hi')</script>\n<b>bold</b>", own: false)), NoErrors);

        html.Should().NotContain("<script>").And.NotContain("<img").And.NotContain("<b>");
        html.Should().Contain("&lt;script&gt;").And.Contain("&lt;img src=x onerror=alert(1)&gt;");
    }
}
