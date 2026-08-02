namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// A fixed, in-memory demo user list — the reference app's entire "identity provider". Two
/// users, one per actor lane this host demonstrates (frontstage applicant, backstage
/// caseworker). Dev-only credentials, documented as such: this is a login for a transient
/// reference host meant to be booted and reset constantly by Playwright, not a pattern to
/// reuse for anything handling real user data. A real host wires its own IdP (Entra, Keycloak,
/// ...) in front of Wayfinder instead.
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

    public static readonly IReadOnlyList<DemoUser> All = [Applicant, Caseworker];

    public static DemoUser? Find(string email) =>
        All.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
}

public sealed record DemoUser(string Email, string DisplayName, string Role);
