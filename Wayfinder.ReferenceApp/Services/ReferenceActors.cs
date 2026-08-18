using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// The two <em>human-facing</em> actor lanes this reference host demonstrates, in NN/g's
/// service-blueprint vocabulary (https://www.nngroup.com/articles/service-blueprints-definition/):
/// "citizen" is frontstage (the applicant's own journey), "caseworker" is backstage (the review
/// queue behind the line of visibility). The third lane — "support processes", a downstream/
/// API-driven actor — is built too (see docs/guides/support-systems.md, the juggling-licence
/// blueprint's "automation" queue, and Services/SupportSystems/SafetyNetUnderwritingClient.cs)
/// but has no entry here: nobody ever renders it to a person, so it needs no
/// <see cref="ActorProfile"/>/capability declaration the way these two do.
/// </summary>
public static class ReferenceActors
{
    public const string CitizenQueue = "citizen";
    public const string CaseworkerQueue = "caseworker";

    /// <summary>
    /// njf-contributions.json's own queue key — deliberately not "caseworker" (see
    /// docs/guides/team-assignment.md): it used to share that key with juggling-licence.json's
    /// queue, disambiguated only by RoleGates, but its assignment policy (assign-to-initiator, one
    /// team) is different enough from juggling-licence's (team-tray) that it now gets a genuinely
    /// distinct queue identity instead of leaning on RoleGates as a same-key workaround.
    /// </summary>
    public const string NjfTeamQueue = "njf-team";

    public const string TenantId = "reference";

    // The citizen (frontstage) lane can render anything in the catalog — built-in or a toolkit
    // extension's own — so its capability declaration is genuinely "everything currently
    // registered", not a curated subset: ComponentTypeRegistry.AllDiscriminators already
    // includes "rating" too by the time this is ever read, since Program.cs calls
    // CustomComponents.Register() as literally its first statement, well before any request (and
    // therefore any ReferenceActors static-field access) can happen. Compare with
    // CaseworkerComponentTypes below, which genuinely IS a deliberate subset — the caseworker's
    // read-only review page has no business rendering a slider or a file-upload control — so
    // that one still lists its types out one by one, each a real registered CLR type rather than
    // a bare string literal.
    private static readonly IReadOnlyList<string> CitizenComponentTypes = ComponentTypeRegistry.AllDiscriminators;

    // Was previously just ["panel", "body", "summary-list"] — accurate for under-review's own
    // top-level components, but not for the text/email/date/number/boolean/file-upload fields
    // its summary-list actually displays as read-only rows: Phase 2's queue-capability
    // validation (see ServiceBlueprintAuthoringService.ValidateQueueCapabilities) descends into
    // every nested component, summary-list children included, so this was already an
    // undeclared-but-rendered gap — unnoticed only because the seed file loads directly via
    // IServiceBlueprintStore, which never runs Validate at all; only a save through the
    // authoring surface (editor/REST/MCP) exercises it.
    private static readonly IReadOnlyList<string> CaseworkerComponentTypes =
        [
            ComponentTypeRegistry.DiscriminatorFor<PanelComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<BodyComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<SummaryListComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<TextInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<EmailComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<DateInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<NumberInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<BooleanComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<FileUploadComponent>(),
            // NJF contributions demo (see docs/guides/bulk-data-review.md) — the review stage's
            // own bulk-data-review component.
            ComponentTypeRegistry.DiscriminatorFor<BulkDataReviewComponent>(),
        ];

    /// <summary>
    /// The applicant can see and act on their own citizen-queue stages only. Once their
    /// instance moves to the caseworker queue, GetCurrent returns ACCESS_DENIED rather than a
    /// read-only peek at the caseworker's stage — crossing a queue boundary read-only is a
    /// distinct capability (see <see cref="CaseworkerProfile"/>'s comment), not something every
    /// profile gets by default just because it's convenient for a status message.
    /// </summary>
    public static ActorProfile CitizenProfile() => new()
    {
        VisibleQueues = [CitizenQueue],
        StartableQueues = [CitizenQueue],
        ActionableQueues = [CitizenQueue],
        RestrictToInstanceOwner = true
    };

    /// <summary>
    /// A caseworker sees and acts on every instance sitting in the caseworker queue, not just
    /// ones they personally started — this is the backstage worklist, shared across the team.
    ///
    /// <see cref="ActorProfile.Capabilities"/> is what actually keeps this team's own worklist
    /// separate from NJF operations' — before this existed, both blueprints' "caseworker" queue
    /// keys collided (both literally "caseworker"), so Casey and Priya could each already see the
    /// *other's* rows purely because of the naming coincidence, regardless of which blueprint they
    /// actually work on. <c>juggling-licence.json</c>'s own "caseworker" queue now declares
    /// <c>roleGates: ["juggling-licence-review"]</c> — see docs/guides/work-allocation.md.
    ///
    /// <see cref="ActorProfile.TeamIds"/> is required now too, not optional polish — that same
    /// queue also declares <c>assignmentPolicy: "team-tray"</c> (see docs/guides/team-assignment.md),
    /// so a profile with no membership of <see cref="DemoTeams.JugglingLicenceReviewers"/> would be
    /// completely locked out of every row in it, unpicked or not.
    /// </summary>
    public static ActorProfile CaseworkerProfile() => new()
    {
        VisibleQueues = [CaseworkerQueue],
        ActionableQueues = [CaseworkerQueue],
        RestrictToInstanceOwner = false,
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "juggling-licence-review" },
        TeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DemoTeams.JugglingLicenceReviewers }
    };

    /// <summary>
    /// A distinct backstage worklist from <see cref="CaseworkerProfile"/>, not a shared one —
    /// NjfOperations is a distinct persona under the same CaseworkerRole, not a separate access
    /// boundary (see DemoUsers.cs's own remarks), but njf-contributions.json's own queue
    /// (<see cref="NjfTeamQueue"/>) is a genuinely different queue from juggling-licence's
    /// (see <see cref="NjfTeamQueue"/>'s own remarks) — so <see cref="ActorProfile.VisibleQueues"/>/
    /// <see cref="ActorProfile.ActionableQueues"/> point at it explicitly rather than inheriting
    /// <see cref="CaseworkerProfile"/>'s.
    ///
    /// Plus a <see cref="ActorProfile.ConcurrencyScopeKey"/> demonstrating "only one bulk load per
    /// juggling authority": every NJF operations user shares this same key, so
    /// GetCurrent/GetCurrentOrStartFresh treat them as one owner for concurrency purposes
    /// regardless of which of them actually submits a file, even though they can already all see
    /// and act on the same shared queue either way.
    ///
    /// <see cref="ActorProfile.Capabilities"/> is explicitly its own set here, not
    /// <see cref="CaseworkerProfile"/>'s — Priya's team is eligible for
    /// <c>njf-contributions.json</c>'s own <see cref="NjfTeamQueue"/> (<c>roleGates: ["njf-contributions-review"]</c>),
    /// not Casey's juggling-licence one, even though both are still the same "caseworker" role at
    /// the auth layer (see DemoUsers.cs).
    /// </summary>
    public static ActorProfile NjfOperationsProfile() => new()
    {
        VisibleQueues = [NjfTeamQueue],
        ActionableQueues = [NjfTeamQueue],
        RestrictToInstanceOwner = false,
        ConcurrencyScopeKey = "njf-contributions-org:njf",
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "njf-contributions-review" },
        TeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DemoTeams.NjfContributionsTeam }
    };

    /// <summary>
    /// Every generic backstage route (the worklist, an item's own page, advance, claim/release)
    /// is shared across every "caseworker"-role persona — it has no per-blueprint knowledge of its
    /// own, so it can't just call <see cref="CaseworkerProfile"/> or <see cref="NjfOperationsProfile"/>
    /// directly without silently locking one persona out of the other's now-capability-gated queue
    /// (see docs/guides/work-allocation.md). Resolves by the demo login itself — a real host would
    /// resolve this from whatever its own team/role directory says about the signed-in user, the
    /// same way <c>tenantId</c>/<c>userId</c> already are. Sam Ops shares Priya's NJF persona
    /// (both resolve to the identical <see cref="NjfOperationsProfile"/> shape — see
    /// docs/guides/team-assignment.md); Jordan Reviewer needs no special case at all, since he
    /// shares Casey's exact <see cref="CaseworkerProfile"/> already, the same "else" branch.
    /// </summary>
    public static ActorProfile ProfileForCaseworkerUser(string userId) =>
        string.Equals(userId, DemoUsers.NjfOperations.Email, StringComparison.OrdinalIgnoreCase)
        || string.Equals(userId, DemoUsers.SecondNjfOperations.Email, StringComparison.OrdinalIgnoreCase)
            ? NjfOperationsProfile()
            : CaseworkerProfile();

    /// <summary>
    /// Friendly name for a <see cref="DemoTeams"/> id — used to render Wayfinder.Engine.Worklist's
    /// own team nav (<c>WorklistOptions.ResolveTeams</c>). Deliberately avoids the substring
    /// "review" — Playwright's own accessible-name matching for the worklist's "Review" link is
    /// substring-based, so a nav link whose own text contained it (the original "Juggling licence
    /// reviewers") ambiguously matched every `getByRole('link', { name: 'Review' })` lookup across
    /// several other specs, a real cross-spec break found live.
    /// </summary>
    public static string TeamDisplayName(string teamId) => teamId switch
    {
        DemoTeams.JugglingLicenceReviewers => "Juggling Licence Team",
        DemoTeams.NjfContributionsTeam => "NJF Contributions Team",
        _ => teamId
    };

    public static IQueueCapabilitiesProvider CapabilitiesProvider() => new StaticQueueCapabilitiesProvider(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CitizenQueue] = CitizenComponentTypes,
            [CaseworkerQueue] = CaseworkerComponentTypes,
            [NjfTeamQueue] = CaseworkerComponentTypes
        });
}
