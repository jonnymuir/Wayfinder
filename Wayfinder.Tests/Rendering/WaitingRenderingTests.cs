using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// A wait screen shows its message, who it is still waiting for, and only the expectations its pace allows: an
/// answer that comes back while they wait says how long is normal, one that comes back later never does.
/// </summary>
public class WaitingRenderingTests
{
    private static string Render(ComponentRenderPayload waiting) =>
        new GovUkComponentRenderer().RenderComponent(waiting, new Dictionary<string, string>());

    [Fact]
    public void WhoTheWaitIsOn_IsShownApartFromTheMessage_AndEscaped()
    {
        var html = Render(new ComponentRenderPayload
        {
            Type = "waiting", Content = "We'll email you when we reply.", WaitingFor = ["The Exchange", "<b>Team</b>"],
        });

        html.Should().Contain("<p class=\"govuk-body\">We'll email you when we reply.</p>");
        html.Should().Contain("Waiting for: The Exchange, &lt;b&gt;Team&lt;/b&gt;");
    }

    [Fact]
    public void AWaitWithNobodyNamed_ShowsOnlyItsMessage()
    {
        Render(new ComponentRenderPayload { Type = "waiting", Content = "Please wait." }).Should().NotContain("Waiting for");
    }

    private static ComponentRenderPayload Waiting(WaitingPace? pace, int? expected = null, int? waited = null, WaitingIcon? icon = null) => new()
    {
        Type = "waiting", Content = "We are checking.", WaitingPace = pace, ExpectedWaitSeconds = expected, WaitedSeconds = waited, WaitingIcon = icon,
    };

    private static IEnumerable<string> VisibleText(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "<p class=\"govuk-body\"(?: data-wayfinder-waiting-phase=\"[a-z-]+\")?>([^<]*)</p>")
            .Select(m => m.Groups[1].Value);

    [Fact]
    public void WhileTheyWait_WithinTheUsualTime_SaysHowLongItUsuallyTakes_AndThatItIsStillWorking()
    {
        var html = Render(Waiting(WaitingPace.WhileTheyWait, expected: 60, waited: 20));

        VisibleText(html).Should().Contain("This usually takes about a minute. This page will update by itself.");
        html.Should().Contain("Still working on it");
        html.Should().Contain("role=\"status\"");
    }

    [Fact]
    public void WhileTheyWait_PastTheUsualTime_SaysItIsTakingLonger()
    {
        VisibleText(Render(Waiting(WaitingPace.WhileTheyWait, expected: 60, waited: 90)))
            .Should().Contain(text => text.StartsWith("This is taking a bit longer than usual.", StringComparison.Ordinal));
    }

    [Fact]
    public void WhileTheyWait_FarPastTheUsualTime_SaysTheyCanLeave()
    {
        VisibleText(Render(Waiting(WaitingPace.WhileTheyWait, expected: 60, waited: 400)))
            .Should().Contain(text => text.Contains("leave this page and come back later", StringComparison.Ordinal));
    }

    [Fact]
    public void Later_SaysTheyCanLeave_AndNeverClaimsItIsTakingLonger()
    {
        var html = Render(Waiting(WaitingPace.Later, waited: 100_000));

        html.Should().Contain("You do not need to stay on this page.");
        html.Should().NotContain("longer than usual").And.NotContain("usually takes").And.NotContain("Still working");
    }

    [Fact]
    public void NoPace_IsNeutral_AndMakesNoTimeClaim()
    {
        var html = Render(Waiting(null, waited: 100_000));

        html.Should().Contain("Still working on it").And.Contain("This page will update by itself.");
        html.Should().NotContain("longer than usual").And.NotContain("usually takes");
    }

    [Fact]
    public void NoIconIsShown_UnlessTheAuthorChoseOne()
    {
        Render(Waiting(WaitingPace.WhileTheyWait)).Should().NotContain("<svg");
    }

    [Fact]
    public void AChosenIcon_IsDecorative_SoScreenReadersHearOnlyTheWords()
    {
        var html = Render(Waiting(WaitingPace.WhileTheyWait, icon: WaitingIcon.Butterfly));

        html.Should().MatchRegex("<span[^>]*aria-hidden=\"true\"[^>]*><svg");
    }

    [Theory]
    [InlineData(5, "a few seconds")]
    [InlineData(30, "about 30 seconds")]
    [InlineData(90, "about a minute")]
    [InlineData(300, "about 5 minutes")]
    public void TheUsualTime_IsSaidInPlainWords(int seconds, string words)
    {
        GovUkWaiting.Duration(seconds).Should().Be(words);
    }
}
