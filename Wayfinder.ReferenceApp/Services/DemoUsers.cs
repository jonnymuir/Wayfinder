namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// A fixed, in-memory demo user list — the reference app's entire "identity provider". Dev-only
/// credentials, documented as such: this is a login for a transient reference host meant to be
/// booted and reset constantly by Playwright, not a pattern to reuse for anything handling real
/// user data. A real host wires its own IdP (Entra, Keycloak, ...) in front of Wayfinder instead.
///
/// Six users, but only two *roles* — the reference app's backstage tooling is deliberately one
/// shared worklist across every service it hosts (see ReferenceActors.CaseworkerProfile's own
/// remarks: "the backstage worklist, shared across the team"), the same way a real organisation
/// often has one case-management tool used by several different back-office teams. NjfOperations
/// is a second *persona* under the same CaseworkerRole, not a second access boundary — the
/// contributions-file demo is staffed by someone at the National Juggling Federation, not the
/// licensing authority's own Casey Caseworker, even though both sign in through the same
/// mechanism and land on the same queue.
///
/// Jamie/Jordan/Sam exist for one reason: proving genuine multi-actor contention (see
/// docs/guides/team-assignment.md), which Casey/Priya alone never exercise — they're
/// capability-partitioned onto different blueprints, so they never compete for the same row. Jamie
/// is a second, otherwise-identical applicant (proves cross-citizen isolation — see
/// ReferenceActors.CitizenProfile's own remarks, previously untested because there was only ever
/// one). Jordan shares Casey's <c>juggling-licence-reviewers</c> team (proves real team-tray
/// contention: both can see an unpicked row, only one can hold it once picked up). Sam shares
/// Priya's <c>njf-contributions-team</c> (proves assign-to-initiator with more than one possible
/// actor: whoever starts a bulk load owns it, the other genuinely cannot).
/// </summary>
public static class DemoUsers
{
    public const string CaseworkerRole = "caseworker";
    public const string ApplicantRole = "applicant";
    public const string DemoPassword = "wayfinder-demo";

    public static readonly DemoUser Applicant = new(
        Email: "applicant@example.test",
        DisplayName: "Alex Applicant",
        Role: ApplicantRole);

    /// <summary>A second, otherwise-identical applicant — proves cross-citizen isolation (see docs/guides/team-assignment.md).</summary>
    public static readonly DemoUser SecondApplicant = new(
        Email: "jamie-applicant@example.test",
        DisplayName: "Jamie Applicant",
        Role: ApplicantRole);

    public static readonly DemoUser Caseworker = new(
        Email: "caseworker@example.test",
        DisplayName: "Casey Caseworker",
        Role: CaseworkerRole);

    /// <summary>Shares Casey's juggling-licence-reviewers team — proves real team-tray contention (see docs/guides/team-assignment.md).</summary>
    public static readonly DemoUser SecondCaseworker = new(
        Email: "jordan-reviewer@example.test",
        DisplayName: "Jordan Reviewer",
        Role: CaseworkerRole);

    public static readonly DemoUser NjfOperations = new(
        Email: "njf-operations@example.test",
        DisplayName: "Priya Shah",
        Role: CaseworkerRole);

    /// <summary>Shares Priya's njf-contributions-team — proves assign-to-initiator with more than one possible actor (see docs/guides/team-assignment.md).</summary>
    public static readonly DemoUser SecondNjfOperations = new(
        Email: "sam-ops@example.test",
        DisplayName: "Sam Ops",
        Role: CaseworkerRole);

    public static readonly IReadOnlyList<DemoUser> All =
        [Applicant, SecondApplicant, Caseworker, SecondCaseworker, NjfOperations, SecondNjfOperations];

    public static DemoUser? Find(string email) =>
        All.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
}

public sealed record DemoUser(string Email, string DisplayName, string Role);
