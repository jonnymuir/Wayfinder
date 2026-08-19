using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Engine.Worklist;

/// <summary>
/// Maps the bulk-data-review component's own REST endpoints (see docs/guides/bulk-data-review.md)
/// — paging/filtering, correcting a row, reverting to last-validated values, downloading the full
/// reconstructed file. A separate call from <see cref="WorklistExtensions.MapWorklist"/> rather
/// than folded into it: <see cref="IBulkDatasetStore"/> is a real DI dependency
/// <c>MapWorklist</c>'s other routes don't need, and forcing every worklist consumer to register a
/// dataset store just to get list/item/advance/pickup/putback would be the wrong coupling. A host
/// that wants bulk-data-review support calls both, with the same prefix:
/// <code>
/// app.MapWorklist(prefix: "/caseworker/queue").RequireAuthorization("Caseworker");
/// app.MapBulkDatasetReview(prefix: "/caseworker/queue").RequireAuthorization("Caseworker");
/// </code>
/// Ported verbatim from Wayfinder.ReferenceApp/Program.cs. Reuses <see cref="WorklistOptions"/> (via
/// <see cref="IOptions{TOptions}"/>, so a host calling this always also calls
/// <see cref="WorklistExtensions.AddWorklist"/>) both to attribute <c>correctedBy</c>/<c>revertedBy</c>
/// and — new for the sync-state feature — to resolve the tenant/access profile
/// <see cref="IProcessManager.SyncBulkDatasetSyncState"/> needs. <c>{blueprintKey}</c> is accepted
/// but never read in any handler — kept in every route pattern anyway, for URL-shape symmetry with
/// <c>GovUkStageJourney.WithBulkDatasetApiUrls</c>, which already builds
/// <c>{prefix}/{blueprintKey}/{instanceId}/bulk-datasets/...</c> from the item page.
///
/// Trust model, unchanged from the reference app's own: every route relies on the host's own
/// auth-gated group (e.g. <c>.RequireAuthorization("Caseworker")</c>) as its only access check —
/// no extra per-instance ownership check here. <see cref="IBulkDatasetStore"/> itself still
/// independently verifies <c>instanceId</c> owns <c>datasetId</c> regardless (defence in depth,
/// throwing <see cref="UnauthorizedAccessException"/>), and both a dataset that doesn't exist and
/// one that belongs to a different instance map to a plain 404, deliberately not distinguished, so
/// a client can't use the response to tell which case it hit.
/// </summary>
public static class BulkDatasetReviewExtensions
{
    public static RouteGroupBuilder MapBulkDatasetReview(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var group = endpoints.MapGroup(prefix);

        group.MapGet("/{blueprintKey}/{instanceId}/bulk-datasets/{datasetId}/summary", async (
            string blueprintKey, string instanceId, string datasetId, IBulkDatasetStore bulkDatasetStore) =>
        {
            try
            {
                var summary = await bulkDatasetStore.GetSummaryAsync(instanceId, datasetId);
                return summary is null ? Results.NotFound() : Results.Ok(summary);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        });

        group.MapGet("/{blueprintKey}/{instanceId}/bulk-datasets/{datasetId}/rows", async (
            string blueprintKey, string instanceId, string datasetId, string? filter, int? page, int? pageSize,
            IBulkDatasetStore bulkDatasetStore) =>
        {
            var parsedFilter = Enum.TryParse<BulkDatasetRowFilter>(filter, ignoreCase: true, out var f)
                ? f
                : BulkDatasetRowFilter.NeedsAttention;
            var pageIndex = Math.Max(page ?? 0, 0);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);

            try
            {
                var result = await bulkDatasetStore.GetRowsAsync(instanceId, datasetId, parsedFilter, pageIndex, size);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        });

        // Both correct/revert below also sync the dataset's own dirty-row count back into
        // FieldValues (see docs/guides/bulk-data-review.md's sync-state section) and hand back the
        // freshly-rendered action-bar fragment in the same round trip — a client swaps it in place
        // rather than needing a second fetch to learn whether e.g. "Accept and finish" just became
        // unavailable. This is a UX nicety only: Advance() independently re-derives route
        // eligibility from FieldValues on every call regardless of what any client renders or
        // caches, so a stale/never-refreshed action bar can never let a since-blocked route through.
        group.MapPost("/{blueprintKey}/{instanceId}/bulk-datasets/{datasetId}/rows/{rowKey}/correct", async (
            string blueprintKey, string instanceId, string datasetId, string rowKey,
            Dictionary<string, string?> correctedValues, HttpContext ctx, IBulkDatasetStore bulkDatasetStore,
            IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            try
            {
                var options = optionsAccessor.Value;
                await bulkDatasetStore.ApplyCorrectionAsync(instanceId, datasetId, rowKey, correctedValues, options.ResolveUserId(ctx));
                return Results.Content(RenderSyncedActionBar(engine, options, ctx, instanceId, datasetId), "text/html");
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{blueprintKey}/{instanceId}/bulk-datasets/{datasetId}/revert", async (
            string blueprintKey, string instanceId, string datasetId, HttpContext ctx,
            IBulkDatasetStore bulkDatasetStore, IProcessManager engine, IOptions<WorklistOptions> optionsAccessor) =>
        {
            try
            {
                var options = optionsAccessor.Value;
                await bulkDatasetStore.RevertCorrectionsAsync(instanceId, datasetId, options.ResolveUserId(ctx));
                return Results.Content(RenderSyncedActionBar(engine, options, ctx, instanceId, datasetId), "text/html");
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        // The fallback path for anywhere a full state recheck already happens without a page
        // reload (paging, filtering) — nothing here depends on the live-swap-after-correct/revert
        // path above being reachable at all (e.g. JavaScript disabled, or the fetch simply lost a
        // race); an explicit re-fetch always gets the current, correct answer, straight from the
        // same rendering RenderForm's own page-load path uses.
        group.MapGet("/{blueprintKey}/{instanceId}/action-bar", (
            string blueprintKey, string instanceId, HttpContext ctx, IProcessManager engine,
            IOptions<WorklistOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            var envelope = engine.GetCurrent(
                blueprintKey, options.ResolveTenantId!(ctx), options.ResolveUserId(ctx), options.ResolveAccessProfile!(ctx), instanceId);
            return Results.Content(RenderActionBarFragment(envelope), "text/html");
        });

        group.MapGet("/{blueprintKey}/{instanceId}/bulk-datasets/{datasetId}/download", async (
            string blueprintKey, string instanceId, string datasetId, IBulkDatasetStore bulkDatasetStore, IServiceRequestFileStorage fileStorage) =>
        {
            ServiceRequestFileReference materialized;
            try
            {
                // A pure human-facing export, not tied to any real blueprint field — targetFieldKey
                // here is just IServiceRequestFileStorage's own partition key, never read back by
                // the engine.
                materialized = await bulkDatasetStore.MaterializeAsync(
                    instanceId, datasetId, targetFieldKey: "bulkDatasetDownload", fileName: "contributions.csv",
                    sanitizeForHumanExport: true);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }

            var stream = await fileStorage.OpenReadAsync(materialized.StorageKey);
            return stream is null ? Results.NotFound() : Results.File(stream, "text/csv", materialized.OriginalFileName);
        });

        return group;
    }

    private static string RenderSyncedActionBar(
        IProcessManager engine, WorklistOptions options, HttpContext ctx, string instanceId, string datasetId)
    {
        var envelope = engine.SyncBulkDatasetSyncState(
            instanceId, options.ResolveTenantId!(ctx), options.ResolveUserId(ctx), options.ResolveAccessProfile!(ctx), datasetId);
        return RenderActionBarFragment(envelope);
    }

    private static string RenderActionBarFragment(ServiceRequestResponseEnvelope envelope) =>
        envelope.Render is { } render
            ? GovUkComponentRenderer.RenderActionButtons(render.AvailableActions, envelope.StateVersion)
            : GovUkComponentRenderer.RenderActionButtons([], envelope.StateVersion);
}
