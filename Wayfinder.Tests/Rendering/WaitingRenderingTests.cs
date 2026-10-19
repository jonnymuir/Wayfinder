using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>A wait screen shows its message, and who it is still waiting for when the engine says.</summary>
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
}
