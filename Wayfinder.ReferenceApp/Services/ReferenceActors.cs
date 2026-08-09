using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>
/// The two actor lanes this reference host demonstrates, in NN/g's service-blueprint
/// vocabulary (https://www.nngroup.com/articles/service-blueprints-definition/): "citizen" is
/// frontstage (the applicant's own journey), "caseworker" is backstage (the review queue
/// behind the line of visibility). A third "support-systems" lane — a downstream/API-driven
/// queue — is a deliberate future addition, not built here.
/// </summary>
public static class ReferenceActors
{
    public const string CitizenQueue = "citizen";
    public const string CaseworkerQueue = "caseworker";
    public const string TenantId = "reference";

    // Each queue's own curated allow-list — deliberately a subset, not "everything the catalog
    // supports" (a caseworker's read-only review page has no business rendering a slider or a
    // file-upload control, say), so this can't just be ComponentTypeRegistry.AllDiscriminators.
    // But every entry still comes FROM the registry via a real CLR type, not a bare string
    // literal — a rename or a typo breaks the build instead of silently drifting out of sync
    // with the real catalog, the exact failure mode ValidateQueueCapabilityDeclarations exists
    // to catch at runtime, caught earlier here at compile time instead.
    private static readonly IReadOnlyList<string> CitizenComponentTypes =
        [
            ComponentTypeRegistry.DiscriminatorFor<FieldsetComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<TextInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<EmailComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<DateInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<NumberInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<DecimalInputComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<BooleanComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<RadiosComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<SliderComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<PanelComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<BodyComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<SummaryListComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<StatGroupComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<ChartComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<InsetTextComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<WarningTextComponent>(),
            ComponentTypeRegistry.DiscriminatorFor<FileUploadComponent>(),
            // "rating" is a toolkit-extension component, not one of Wayfinder's own built-ins —
            // see Services/CustomComponents.cs. Kept as a plain discriminator constant (not a
            // ComponentTypeRegistry.DiscriminatorFor<T>() call) since it lives outside this
            // assembly's compile-time reach anyway; the point still stands either way — a nice
            // touch showing different queues can declare genuinely different capabilities.
            CustomComponents.RatingDiscriminator,
        ];

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

    public static IQueueCapabilitiesProvider CapabilitiesProvider() => new StaticQueueCapabilitiesProvider(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CitizenQueue] = CitizenComponentTypes,
            [CaseworkerQueue] = CaseworkerComponentTypes
        });
}
