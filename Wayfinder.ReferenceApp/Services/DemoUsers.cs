namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// A fixed, in-memory demo user list — the reference app's entire "identity provider". Dev-only
/// credentials, documented as such: this is a login for a transient reference host meant to be
/// booted and reset constantly by Playwright, not a pattern to reuse for anything handling real
/// user data. A real host wires its own IdP (Entra, Keycloak, ...) in front of Wayfinder instead.
///
/// Three users, but only two *roles* — the reference app's backstage tooling is deliberately one
/// shared worklist across every service it hosts (see ReferenceActors.CaseworkerProfile's own
/// remarks: "the backstage worklist, shared across the team"), the same way a real organisation
/// often has one case-management tool used by several different back-office teams. NjfOperations
/// is a second *persona* under the same CaseworkerRole, not a second access boundary — the
/// contributions-file demo is staffed by someone at the National Juggling Federation, not the
/// licensing authority's own Casey Caseworker, even though both sign in through the same
/// mechanism and land on the same queue.
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

    public static readonly DemoUser Caseworker = new(
        Email: "caseworker@example.test",
        DisplayName: "Casey Caseworker",
        Role: CaseworkerRole);

    public static readonly DemoUser NjfOperations = new(
        Email: "njf-operations@example.test",
        DisplayName: "Priya Shah",
        Role: CaseworkerRole);

    public static readonly IReadOnlyList<DemoUser> All = [Applicant, Caseworker, NjfOperations];

    public static DemoUser? Find(string email) =>
        All.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
}

public sealed record DemoUser(string Email, string DisplayName, string Role);
