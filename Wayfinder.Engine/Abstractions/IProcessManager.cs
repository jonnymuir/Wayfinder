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
    /// The caseworker worklist. <paramref name="userId"/> is who's asking — a cursor picked up by
    /// someone else is hidden from this call entirely (see <see cref="PickupWorkItem"/> and
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
    /// A team's own aggregate view of everything it owns (see docs/guides/team-assignment.md) —
    /// every row belonging to a queue with <c>QueueDefinition.OwningTeamId == teamId</c>, whether
    /// still sitting unpicked in the tray or already picked up by a specific teammate. Unlike
    /// <see cref="GetQueueWorkItems"/> (which only ever shows what's actionable to one calling
    /// <c>userId</c>), this is "what does my team currently own", not "what can I personally do
    /// right now". <paramref name="accessProfile"/> must itself be a member of <paramref name="teamId"/>
    /// (<see cref="ActorProfile.IsTeamMember"/>) — otherwise an empty envelope, the same permissive-
    /// method/host-enforces-the-denial contract already used for tenant scoping elsewhere in this
    /// interface. Only ever returns rows from a team-owned queue — a legacy queue (no
    /// <c>AssignmentPolicy</c>) has no team to own it, so never appears here.
    /// </summary>
    QueueWorkListEnvelope GetTeamWorkItems(
        string tenantId,
        string teamId,
        ActorProfile accessProfile,
        IReadOnlyCollection<QueueWorkItemStatus>? statuses = null,
        QueueWorkListSort sort = QueueWorkListSort.Default,
        string? searchText = null,
        int pageIndex = 0,
        int pageSize = 20);

    /// <summary>
    /// Picks up one specific work item for <paramref name="userId"/> — becomes its sole owner, hidden
    /// from every other actor sharing the queue until put back (see <see cref="PutbackWorkItem"/>).
    /// No <c>expectedStateVersion</c>: unlike <see cref="Advance(string,string,string,ActorProfile,string,int,Dictionary{string,object?}?)"/>,
    /// picking up carries no field edits to lose, so the engine reads fresh and retries its own
    /// internal compare-and-swap a bounded number of times rather than asking the caller to supply
    /// a version. Errors (as <c>ServiceRequestResponseEnvelope.Problems</c>' <c>Code</c>):
    /// <c>INSTANCE_NOT_FOUND</c>, <c>ACCESS_DENIED</c>, <c>INVALID_TRANSITION</c> (the row isn't
    /// visible to this actor at all — this is what capability ineligibility surfaces as too, since
    /// both go through the same resolution), <c>PICKUP_NOT_AVAILABLE</c> (Waiting/Done/owner-restricted
    /// — nothing to pick up), <c>ALREADY_PICKED_UP</c> (someone else holds it), <c>PICKUP_CONFLICT</c>
    /// (lost the internal race after retrying). See docs/guides/work-allocation.md.
    /// </summary>
    ServiceRequestResponseEnvelope PickupWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);

    /// <summary>
    /// Puts back a pickup <paramref name="userId"/> currently holds on <paramref name="cursorId"/>,
    /// to the shared pool — a no-op success if already not picked up.
    /// <c>ALREADY_PICKED_UP_BY_OTHER</c> if held by someone else (putting back is self-service only;
    /// see docs/guides/work-allocation.md's reassignment seam for the future manager-initiated case).
    /// </summary>
    ServiceRequestResponseEnvelope PutbackWorkItem(
        string instanceId, string cursorId, string tenantId, string userId, ActorProfile accessProfile);

    /// <summary>
    /// Writes <paramref name="updates"/> straight into <c>FieldValues</c> — no cursor move, no
    /// onEnter/onExit actions, not even a stage re-evaluation beyond what merging the values
    /// implies. For a host extension that computes something outside the normal
    /// <see cref="Advance(string,string,string,ActorProfile,string,int,Dictionary{string,object?}?)"/>
    /// pipeline (a bulk-dataset row correction is the first caller — see
    /// docs/guides/bulk-data-review.md) but still needs it to be genuinely visible to every
    /// <c>showWhen</c>/calculation evaluation from this point on, the same way any other field
    /// value already is. No new recalculation step exists to trigger, or is needed: both
    /// <c>Advance</c> and every render path already re-derive <c>AvailableActions</c> and every
    /// calculated field fresh from <c>FieldValues</c> on every single call, never from a cache —
    /// so a value written here is picked up automatically, everywhere, starting with the very next
    /// request. In particular this means <c>Advance</c>'s own trigger resolution — which already
    /// fails closed against a freshly-rebuilt eligible-action set, not whatever a client claims —
    /// enforces the *result* of a sync exactly as strictly as it enforces every other field, with
    /// no separate authorization code required here.
    ///
    /// <para><b>Authorization boundary</b>: every key in <paramref name="updates"/> must be
    /// declared under the current blueprint's own <c>calculations.fields</c> with
    /// <c>source: "service"</c> — the same category <c>errorCountField</c>/<c>warningCountField</c>
    /// (see docs/guides/bulk-data-review.md) already require for <c>showWhen</c> visibility. This
    /// is deliberate, not incidental: it reuses an existing, already-understood blueprint concept
    /// as the sole authorization boundary, rather than inventing a second one. A captured input or
    /// a formula-computed field can never be written this way — only a real <c>Advance</c> touches
    /// those. Returns <c>NOT_SERVICE_FIELD</c> for any key that isn't so declared.</para>
    ///
    /// CAS-retried the same bounded number of times as <see cref="PickupWorkItem"/> — no
    /// <c>expectedStateVersion</c> parameter, since (like a pickup) this carries no user-typed
    /// field edits a caller could lose by retrying against fresher data.
    /// </summary>
    ServiceRequestResponseEnvelope SyncServiceFields(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile,
        Dictionary<string, object?> updates);

    /// <summary>
    /// The bulk-dataset-specific caller of <see cref="SyncServiceFields"/> — resolves which
    /// <c>bulk-dataset-ingest</c> action declared <paramref name="datasetId"/> (the one whose own
    /// <c>datasetIdField</c>, read from this instance's current <c>FieldValues</c>, equals it —
    /// the same cross-reference <c>bulk-dataset-materialize</c>'s own <c>datasetIdField</c> match
    /// already relies on), reads that action's <c>dirtyCountField</c> param, and syncs it to the
    /// dataset's current <c>BulkDatasetSummary.DirtyRowCount</c>. A no-op — returns
    /// <see cref="GetCurrent(string,string,string,ActorProfile,string,string)"/>'s own fresh
    /// envelope, nothing written — when either the dataset can't be matched to a declaring action
    /// or that action never declared <c>dirtyCountField</c> (the feature is opt-in, like every
    /// other declared count field). Deliberately narrow: never touches <c>errorCountField</c>/
    /// <c>warningCountField</c>/<c>acceptedCountField</c> — those are SafetyNet's own verdict and
    /// must only ever change via a real ingest. See docs/guides/bulk-data-review.md.
    /// </summary>
    ServiceRequestResponseEnvelope SyncBulkDatasetSyncState(
        string instanceId, string tenantId, string userId, ActorProfile accessProfile, string datasetId);

    /// <summary>
    /// For an automated/scaled-out caller — atomically picks up the single oldest eligible,
    /// not-picked-up, Actionable row this profile can see across every instance, or <see langword="null"/>
    /// if nothing is currently available. Safe against multiple concurrent workers calling this
    /// simultaneously (see <see cref="IServiceRequestStore.TrySaveIfVersionMatches"/>) — no two
    /// callers can ever be handed the same row. Deliberately simple: no lease, no heartbeat, no
    /// auto-expiry back to the pool if the caller that picked up a row never finishes it — see
    /// docs/guides/work-allocation.md for why v1 stops here.
    /// </summary>
    QueueWorkItem? PickupNextAvailableWorkItem(string tenantId, string userId, ActorProfile accessProfile);

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
