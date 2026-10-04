namespace Wayfinder.Engine.Services;

/// <summary>The values a queue's <c>assignmentPolicy</c> can take. See docs/guides/team-assignment.md.</summary>
internal static class AssignmentPolicies
{
    /// <summary>Whoever's action lands work here becomes its individual owner immediately.</summary>
    public const string AssignToInitiator = "assign-to-initiator";

    /// <summary>Work lands owned by the team as a whole, pickable by any member, actionable only once picked up.</summary>
    public const string TeamTray = "team-tray";
}
