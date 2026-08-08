using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

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

    private static readonly IReadOnlyList<string> CitizenComponentTypes =
        ["fieldset", "text", "email", "date", "number", "decimal", "boolean", "radio", "slider",
         "panel", "body", "summary-list", "stat-group", "chart", "inset-text", "warning-text",
         "file-upload",
         // "rating" is a toolkit-extension component, not one of Wayfinder's own built-ins —
         // see Services/CustomComponents.cs.
         CustomComponents.RatingDiscriminator];

    // Was previously just ["panel", "body", "summary-list"] — accurate for under-review's own
    // top-level components, but not for the text/email/date/number/boolean/file-upload fields
    // its summary-list actually displays as read-only rows: Phase 2's queue-capability
    // validation (see ServiceBlueprintAuthoringService.ValidateQueueCapabilities) descends into
    // every nested component, summary-list children included, so this was already an
    // undeclared-but-rendered gap — unnoticed only because the seed file loads directly via
    // IServiceBlueprintStore, which never runs Validate at all; only a save through the
    // authoring surface (editor/REST/MCP) exercises it.
    private static readonly IReadOnlyList<string> CaseworkerComponentTypes =
        ["panel", "body", "summary-list", "text", "email", "date", "number", "boolean", "file-upload"];

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
