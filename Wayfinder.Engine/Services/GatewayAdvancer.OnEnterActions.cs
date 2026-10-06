using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.FieldValueMerge;

namespace Wayfinder.Engine.Services;

internal sealed partial class GatewayAdvancer
{
    private sealed record OnEnterResult(
        Dictionary<string, object?> FieldValues,
        List<SupportSystemInvocation> Invocations,
        bool Failed);

    /// <summary>
    /// Runs the <c>onEnter</c> actions of every branch that landed straight on a stage (not another
    /// gateway) — the automation-queue branch of a "send to support system" split, e.g. See
    /// ExecuteOnEnterSupportSystemActions's own remarks for why this only runs for multi-cursor
    /// branches, not the single-cursor path. Bulk-dataset actions run first, per cursor, so a
    /// bulk-dataset-materialize action's refreshed file (a resubmission loop re-firing this same
    /// split) is what support-system-call reads — see ExecuteOnEnterBulkDatasetActions's own remarks.
    /// </summary>
    private OnEnterResult RunOnEnterActions(
        ServiceRequest instance,
        ServiceBlueprint definition,
        IEnumerable<RequestCursor> newCursors,
        Dictionary<string, object?> fieldValues)
    {
        var invocations = new List<SupportSystemInvocation>();
        foreach (var cursor in newCursors.Where(cursor => !cursor.IsAtGateway))
        {
            var bulkDatasetUpdates = bulkDatasets.ExecuteOnEnterBulkDatasetActions(instance.InstanceId, definition, fieldValues, cursor);
            if (bulkDatasetUpdates.Count > 0)
            {
                fieldValues = Merge(fieldValues, bulkDatasetUpdates);
            }

            var batch = supportSystems.ExecuteOnEnterSupportSystemActions(instance.InstanceId, definition, fieldValues, cursor);
            if (batch.Failed)
            {
                return new OnEnterResult(fieldValues, invocations, Failed: true);
            }

            invocations.AddRange(batch.Invocations);
        }

        return new OnEnterResult(fieldValues, invocations, Failed: false);
    }

    /// <summary>
    /// Nothing is saved: the visitor is shown the stage they submitted (<paramref name="submitted"/> still sits on
    /// it, carrying what they typed) with a try-again message.
    /// </summary>
    private ServiceRequestResponseEnvelope SupportCallFailed(
        ServiceRequest submitted,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId) =>
        envelopes.BuildEnvelope(submitted, definition, accessProfile, userId)
            with { Problems = [SupportCallBatch.UnavailableProblem(submitted.CurrentStage)] };
}
