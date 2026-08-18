namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// The demo teams this reference host's queues are owned by (see docs/guides/team-assignment.md) —
/// mirrors <see cref="DemoUsers"/>'s own "just enough to demonstrate the real thing" shape. A real
/// host would resolve team membership from its own directory/HR system, the same way it already
/// resolves everything else <see cref="ReferenceActors"/> hand-wires for this demo.
/// </summary>
public static class DemoTeams
{
    /// <summary>Owns njf-contributions.json's own <see cref="ReferenceActors.NjfTeamQueue"/> — assign-to-initiator.</summary>
    public const string NjfContributionsTeam = "njf-contributions-team";

    /// <summary>Owns juggling-licence.json's own <see cref="ReferenceActors.CaseworkerQueue"/> — team-tray.</summary>
    public const string JugglingLicenceReviewers = "juggling-licence-reviewers";
}
