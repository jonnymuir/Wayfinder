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
    /// The caseworker worklist. <paramref name="userId"/> is who's asking — a cursor claimed by
    /// someone else is hidden from this call entirely (see <see cref="ClaimWorkItem"/> and
    /// docs/guides/work-allocation.md). <paramref name="statuses"/> defaults (when
    /// <see langword="null"/>) to <c>{Actionable, Waiting}</c> — today's exact view; pass an
    /// explicit, possibly-empty collection to override it (see docs/guides/queue-worklist-filtering.md
    /// for the null-vs-empty distinction). <paramref name="searchText"/> matches case-insensitively
    /// across instance id, blueprint/stage display name, and every raw field value.
    /// </summary>
    QueueWorkListEnvelope GetQueueWorkItems(
        string userId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20);

    /// <summary>
    /// Claims one specific work item for <paramref name="userId"/> — becomes its sole owner, hidden
    /// from every other actor sharing the queue until released (see <see cref="ReleaseWorkItem"/>).
    /// No <c>expectedStateVersion</c>: unlike <see cref="Advance(string,string,string,ActorProfile,string,int,Dictionary{string,object?}?)"/>,
    /// claiming carries no field edits to lose, so the engine reads fresh and retries its own
    /// internal compare-and-swap a bounded number of times rather than asking the caller to supply
    /// a version. Errors (as <c>ServiceRequestResponseEnvelope.Problems</c>' <c>Code</c>):
    /// <c>INSTANCE_NOT_FOUND</c>, <c>ACCESS_DENIED</c>, <c>INVALID_TRANSITION</c> (the row isn't
    /// visible to this actor at all — this is what capability ineligibility surfaces as too, since
    /// both go through the same resolution), <c>NOT_CLAIMABLE</c> (Waiting/Done/owner-restricted —
    /// nothing to claim), <c>ALREADY_CLAIMED</c> (someone else holds it), <c>CLAIM_CONFLICT</c>
    /// (lost the internal race after retrying). See docs/guides/work-allocation.md.
    /// </summary>
    ServiceRequestResponseEnvelope ClaimWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);

    /// <summary>
    /// Releases a claim <paramref name="userId"/> currently holds on <paramref name="cursorId"/>,
    /// back to the shared pool — a no-op success if already unclaimed.
    /// <c>ALREADY_CLAIMED_BY_OTHER</c> if held by someone else (release is self-service only; see
    /// docs/guides/work-allocation.md's reassignment seam for the future manager-initiated case).
    /// </summary>
    ServiceRequestResponseEnvelope ReleaseWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);

    /// <summary>
    /// For an automated/scaled-out caller — atomically claims the single oldest eligible,
    /// unclaimed, Actionable row this profile can see across every instance, or <see langword="null"/>
    /// if nothing is currently available. Safe against multiple concurrent workers calling this
    /// simultaneously (see <see cref="IServiceRequestStore.TrySaveIfVersionMatches"/>) — no two
    /// callers can ever be handed the same row. Deliberately simple: no lease, no heartbeat, no
    /// auto-expiry back to the pool if the caller that claimed a row never finishes it — see
    /// docs/guides/work-allocation.md for why v1 stops here.
    /// </summary>
    QueueWorkItem? ClaimNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile);

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
