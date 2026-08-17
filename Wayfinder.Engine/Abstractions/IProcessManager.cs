using Wayfinder.Models.ServiceDesign;
using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Abstractions;

public interface IProcessManager
{
    ServiceRequestResponseEnvelope GetCurrent(
        string blueprintKey,
        string tenantId,
        string userId,
        string? instanceId = null,
        string? action = null);

    ServiceRequestResponseEnvelope GetCurrent(
        string blueprintKey,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string? instanceId = null,
        string? action = null);

    /// <summary>
    /// A distinct "start a new one" affordance, as opposed to <c>GetCurrent</c>'s "continue where
    /// I left off" — reinstates a non-terminal existing instance exactly as ambient
    /// <c>GetCurrent</c> already does, but genuinely starts fresh once the existing one has
    /// reached a terminal stage, rather than returning that stale confirmation forever. See
    /// <c>ProcessManagerEngine.GetCurrentOrStartFresh</c>'s own remarks for why this needed to be
    /// a new method rather than a change to the existing explicit <c>action: "start-new"</c>.
    /// </summary>
    ServiceRequestResponseEnvelope GetCurrentOrStartFresh(
        string blueprintKey, string tenantId, string userId, ActorProfile accessProfile);

    ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues);

    ServiceRequestResponseEnvelope Advance(
        string instanceId,
        string tenantId,
        string userId,
        ActorProfile accessProfile,
        string action,
        int expectedStateVersion,
        Dictionary<string, object?>? fieldValues);

    ServiceRequestListEnvelope GetInstances(string tenantId, string userId);

    /// <summary>
    /// Re-keys every instance owned by <paramref name="fromUserId"/> onto
    /// <paramref name="toUserId"/> and marks each as authenticated — for a visitor who was
    /// browsing anonymously and has just signed in, so their in-progress instance survives
    /// as a resumable one instead of being orphaned under an identity nothing will ever
    /// resolve to again. An instance is left alone (not claimed) if <paramref name="toUserId"/>
    /// already owns an instance of that same blueprint — claiming would silently discard
    /// whichever the caller didn't return here. Returns the claimed instance ids.
    /// </summary>
    IReadOnlyList<string> ClaimInstances(string tenantId, string fromUserId, string toUserId);

    /// <summary>
    /// The caseworker worklist. <paramref name="statuses"/> defaults (when <see langword="null"/>)
    /// to <c>{Actionable, Waiting}</c> — today's exact view; pass an explicit, possibly-empty
    /// collection to override it (see docs/guides/queue-worklist-filtering.md for the
    /// null-vs-empty distinction). <paramref name="searchText"/> matches case-insensitively across
    /// instance id, blueprint/stage display name, and every raw field value.
    /// </summary>
    QueueWorkListEnvelope GetQueueWorkItems(
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20);

    IEnumerable<ServiceRequest> GetAllInstances();

    IEnumerable<ServiceBlueprint> GetAllDefinitions();

    ServiceBlueprint? GetDefinition(string key);

    /// <summary>Registers or updates a definition — an upsert. Always returns true.</summary>
    bool UpdateDefinition(string key, ServiceBlueprint updated);

    /// <summary>Removes a definition. Returns false if it wasn't registered.</summary>
    bool RemoveDefinition(string key);

    bool Reset(string instanceId);

    void ResetAll();
}
