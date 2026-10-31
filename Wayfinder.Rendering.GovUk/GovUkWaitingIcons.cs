using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// The line drawings behind <see cref="WaitingIcon"/>: 24px, one stroke weight, drawn in the text colour so they sit
/// with the GOV.UK typography rather than on top of it. Decorative only; the words beside them carry the meaning.
/// </summary>
internal static class GovUkWaitingIcons
{
    public static string Svg(WaitingIcon icon) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" focusable="false" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">{Paths(icon)}</svg>
        """;

    private static string Paths(WaitingIcon icon) => icon switch
    {
        WaitingIcon.Hourglass => """<path d="M6 3h12M6 21h12M7 3v3a5 5 0 0 0 10 0V3M7 21v-3a5 5 0 0 1 10 0v3"/>""",
        WaitingIcon.Search => """<circle cx="11" cy="11" r="6"/><path d="M15.5 15.5 20 20"/>""",
        WaitingIcon.Document => """<path d="M7 3h7l4 4v14H7z"/><path d="M14 3v4h4M10 12h5M10 16h5"/>""",
        WaitingIcon.Butterfly => """<path d="M12 8v10M12 8l-1.5-3M12 8l1.5-3"/><path d="M12 10C9.5 6 4.5 5 4 8s3 5 8 4"/><path d="M12 10c2.5-4 7.5-5 8-2s-3 5-8 4"/><path d="M12 12.5c-4 0-6.5 1.5-6 4s4 2 6-2"/><path d="M12 12.5c4 0 6.5 1.5 6 4s-4 2-6-2"/>""",
        WaitingIcon.Hedgehog => """<path d="M4 17c0-5 3.5-8 8-8 3 0 5 1.5 6.5 4l2.5 1.5-2 1.5c-.5.7-1.3 1-2 1z"/><path d="M5.5 12.5 4 10.5M8 10 7 7.5M11 9.2V6.5M14 9.5l1-2.5M8 17v1.5M14 17v1.5"/><circle cx="17.5" cy="13" r=".6" fill="currentColor"/>""",
        _ => "",
    };
}
