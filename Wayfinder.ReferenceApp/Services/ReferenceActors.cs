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
    /// </summary>
    public static ActorProfile CaseworkerProfile() => new()
    {
        VisibleQueues = [CaseworkerQueue],
        ActionableQueues = [CaseworkerQueue],
        RestrictToInstanceOwner = false
    };

    /// <summary>
    /// Same backstage worklist as <see cref="CaseworkerProfile"/> — NjfOperations is a distinct
    /// persona under the same CaseworkerRole, not a separate access boundary (see
    /// DemoUsers.cs's own remarks) — plus a <see cref="ActorProfile.ConcurrencyScopeKey"/>
    /// demonstrating "only one bulk load per juggling authority": every NJF operations user
    /// shares this same key, so GetCurrent/GetCurrentOrStartFresh treat them as one owner for
    /// concurrency purposes regardless of which of them actually submits a file, even though
    /// they can already all see and act on the same shared queue either way.
    /// </summary>
    public static ActorProfile NjfOperationsProfile() => CaseworkerProfile() with
    {
        ConcurrencyScopeKey = "njf-contributions-org:njf"
    };

    public static IQueueCapabilitiesProvider CapabilitiesProvider() => new StaticQueueCapabilitiesProvider(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CitizenQueue] = CitizenComponentTypes,
            [CaseworkerQueue] = CaseworkerComponentTypes
        });
}
