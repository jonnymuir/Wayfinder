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
        ["fieldset", "text", "email", "date", "number", "decimal", "boolean", "panel", "body", "summary-list"];

    private static readonly IReadOnlyList<string> CaseworkerComponentTypes =
        ["panel", "body", "summary-list"];

    /// <summary>
    /// The applicant can see and act on their own citizen-queue stages, and can see (but not
    /// act on) the caseworker-queue stage their application is waiting at — the frontstage
    /// visitor's view of "your application is with a caseworker", not a way to act on it.
    /// </summary>
    public static ActorProfile CitizenProfile() => new()
    {
        VisibleQueues = [CitizenQueue, CaseworkerQueue],
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
