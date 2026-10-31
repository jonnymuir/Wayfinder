using System.Globalization;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// The waiting page at a join. What it promises depends on the join's <see cref="WaitingPace"/>: a wait that comes
/// back while the person waits says how long is normal and when it is taking longer, a wait that comes back later
/// says they can leave, and an unset pace stays neutral. Everything is rendered for the moment the page was built;
/// wayfinder-waiting.js moves it on as time passes.
/// </summary>
public static class GovUkWaiting
{
    private const string StillWorking = "Still working on it";
    private const string Offline = "We cannot reach the service at the moment. We will keep trying.";
    private const string UpdatesByItself = "This page will update by itself.";

    public static string Render(ComponentRenderPayload component)
    {
        // Content is the join's own authored waitingContent, already sanitized, so it is rendered raw like body text.
        var message = string.IsNullOrEmpty(component.Content)
            ? "This may take a few minutes. You do not need to do anything else right now."
            : component.Content;
        var waitingForHtml = component.WaitingFor is { Count: > 0 } waitingFor
            ? $"""<p class="govuk-body">Waiting for: {GovUk.Esc(string.Join(", ", waitingFor))}</p>"""
            : "";
        var deferHtml = component.AllowDefer == true && !string.IsNullOrEmpty(component.DeferMessage)
            ? $"""<p class="govuk-body">{GovUk.Esc(component.DeferMessage)}</p>"""
            : "";
        var pollAttr = component.PollIntervalMs is { } poll ? $" data-wayfinder-poll-interval-ms=\"{poll}\"" : "";
        var (pace, status) = component.WaitingPace switch
        {
            WaitingPace.WhileTheyWait => ("while-they-wait", WhileTheyWaitStatus(component)),
            WaitingPace.Later => ("later", LaterStatus(component.WaitingIcon)),
            _ => ("neutral", Working(component.WaitingIcon, animated: true) + Phrase(UpdatesByItself)),
        };
        return $"""
            <div class="govuk-inset-text wayfinder-waiting" data-wayfinder-waiting data-wayfinder-waiting-pace="{pace}"{pollAttr}>
              <p class="govuk-body">{message}</p>
              {waitingForHtml}
              <div role="status" aria-live="polite" aria-atomic="true" data-wayfinder-waiting-status>
                {status}
              </div>
              {deferHtml}
            </div>
            """;
    }

    /// <summary>The phase a wait is in once it has run for <paramref name="waitedSeconds"/> of an expected <paramref name="expectedSeconds"/>.</summary>
    public static string Phase(int waitedSeconds, int expectedSeconds) =>
        waitedSeconds > expectedSeconds * 3 ? "much-longer"
        : waitedSeconds > expectedSeconds ? "longer"
        : "usual";

    private static string WhileTheyWaitStatus(ComponentRenderPayload component)
    {
        var expected = component.ExpectedWaitSeconds is int seconds and > 0 ? seconds : 60;
        var waited = component.WaitedSeconds ?? 0;
        var current = Phase(waited, expected);
        string PhaseLine(string phase, string text) =>
            $"""<p class="govuk-body" data-wayfinder-waiting-phase="{phase}"{(phase == current ? "" : " hidden")}>{text}</p>""";
        return $"""
            <div data-wayfinder-waiting-clock data-wayfinder-waiting-expected-seconds="{expected}" data-wayfinder-waiting-waited-seconds="{waited}">
              {Working(component.WaitingIcon, animated: true)}
              {PhaseLine("usual", $"This usually takes {Duration(expected)}. {UpdatesByItself}")}
              {PhaseLine("longer", $"This is taking a bit longer than usual. You do not need to do anything. {UpdatesByItself}")}
              {PhaseLine("much-longer", "This is taking much longer than usual. You can keep waiting, or leave this page and come back later.")}
            </div>
            """;
    }

    private static string LaterStatus(WaitingIcon? icon) =>
        (icon is null ? "" : Working(icon, animated: false, label: "We are working on it")) +
        Phrase("You do not need to stay on this page. Come back at any time to check progress, or keep it open and it will update by itself.");

    private static string Working(WaitingIcon? icon, bool animated, string label = StillWorking)
    {
        var motion = animated ? " wayfinder-waiting__indicator--animated" : "";
        var indicator = icon is { } chosen
            ? $"""<span class="wayfinder-waiting__indicator wayfinder-waiting__indicator--icon{motion}" aria-hidden="true">{GovUkWaitingIcons.Svg(chosen)}</span>"""
            : $"""<span class="wayfinder-waiting__indicator wayfinder-waiting__indicator--dot{motion}" aria-hidden="true"></span>""";
        return $"""
            <p class="govuk-body govuk-!-font-weight-bold wayfinder-waiting__working">{indicator}<span data-wayfinder-waiting-working>{label}</span><span data-wayfinder-waiting-offline hidden>{Offline}</span></p>
            """;
    }

    private static string Phrase(string text) => $"""<p class="govuk-body">{text}</p>""";

    /// <summary>"a few seconds", "about 30 seconds", "about a minute", "about 5 minutes", "about 2 hours".</summary>
    public static string Duration(int seconds) => seconds switch
    {
        <= 10 => "a few seconds",
        < 60 => $"about {seconds.ToString(CultureInfo.InvariantCulture)} seconds",
        < 120 => "about a minute",
        < 3600 => $"about {(seconds / 60).ToString(CultureInfo.InvariantCulture)} minutes",
        < 7200 => "about an hour",
        _ => $"about {(seconds / 3600).ToString(CultureInfo.InvariantCulture)} hours",
    };
}
