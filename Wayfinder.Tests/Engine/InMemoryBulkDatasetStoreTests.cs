using System.Text;
using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Covers <see cref="InMemoryBulkDatasetStore"/> in isolation — streaming ingest against a
/// declared column schema, row-key correlation/structural-issue handling, paging, the
/// correction overlay (never an in-place overwrite of what was ingested), materialize round-trip
/// fidelity, and the two security/performance guarantees called out as first-class in
/// docs/guides/bulk-data-review.md: CSV-formula-injection sanitization only on the human-export
/// path, and cap enforcement as a clean failure result rather than a crash.
/// </summary>
public class InMemoryBulkDatasetStoreTests
{
    private const string InstanceId = "instance-1";

    private static readonly IReadOnlyList<BulkDatasetColumnDescriptor> Columns =
    [
        new() { Key = "memberRef", Title = "Member ref", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.RowKey },
        new() { Key = "memberName", Title = "Name", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.Data, Editable = true },
        new() { Key = "monthlyContribution", Title = "Monthly contribution", ValueKind = ComponentPropertyValueKind.Number, Role = BulkDatasetColumnRole.Data, Editable = true },
        new() { Key = "safetyNetMemberId", Title = "SafetyNet member ID", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.ResponseMatchedId },
        new() { Key = "errorText", Title = "Errors", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.ResponseError },
        new() { Key = "warningText", Title = "Warnings", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.ResponseWarning },
    ];

    private static (InMemoryBulkDatasetStore Store, InMemoryServiceRequestFileStorage FileStorage) MakeStore(
        BulkDatasetIngestOptions? options = null)
    {
        var fileStorage = new InMemoryServiceRequestFileStorage();
        return (new InMemoryBulkDatasetStore(fileStorage, options), fileStorage);
    }

    private static async Task<ServiceRequestFileReference> SaveCsvAsync(
        InMemoryServiceRequestFileStorage fileStorage, string csv, string fieldKey = "contributionsFile")
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var storageKey = await fileStorage.SaveAsync(InstanceId, fieldKey, stream, "contributions.csv");
        return new ServiceRequestFileReference
        {
            StorageKey = storageKey,
            OriginalFileName = "contributions.csv",
            ContentType = "text/csv",
            SizeBytes = csv.Length,
        };
    }

    private const string Header = "memberRef,memberName,monthlyContribution,safetyNetMemberId,errorText,warningText";

    [Fact]
    public async Task Ingest_ValidFile_ProducesCorrectSummary()
    {
        var (store, fileStorage) = MakeStore();
        var csv = string.Join('\n',
            Header,
            "NJF-001,Alice,25.00,SN-1,,",
            "NJF-002,Bob,25.00,SN-2,Missing DOB,",
            "NJF-003,Cara,50.00,SN-3,,Contribution outside expected band");
        var file = await SaveCsvAsync(fileStorage, csv);

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeTrue();
        result.Summary!.TotalRowCount.Should().Be(3);
        result.Summary.ErrorRowCount.Should().Be(1);
        result.Summary.WarningRowCount.Should().Be(1);
        result.Summary.AcceptedRowCount.Should().Be(1);
    }

    [Fact]
    public async Task Ingest_MissingDeclaredColumn_Fails()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, "memberRef,memberName\nNJF-001,Alice");

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("monthlyContribution");
    }

    [Fact]
    public async Task Ingest_EmptyFile_Fails()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, "");

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_MissingRowKeyValue_IsStructuralAttentionRow_NotACrash()
    {
        var (store, fileStorage) = MakeStore();
        var csv = string.Join('\n', Header, ",Alice,25.00,SN-1,,");
        var file = await SaveCsvAsync(fileStorage, csv);

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeTrue();
        var page = await store.GetRowsAsync(InstanceId, result.DatasetId!, BulkDatasetRowFilter.All, 0, 10);
        page!.Rows.Should().ContainSingle(r => r.StructuralIssue != null && r.HasError);
    }

    [Fact]
    public async Task Ingest_DuplicateRowKey_IsStructuralAttentionRowOnSecondOccurrence()
    {
        var (store, fileStorage) = MakeStore();
        var csv = string.Join('\n',
            Header,
            "NJF-001,Alice,25.00,SN-1,,",
            "NJF-001,Alice again,25.00,SN-1,,");
        var file = await SaveCsvAsync(fileStorage, csv);

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeTrue();
        result.Summary!.TotalRowCount.Should().Be(2);
        var page = await store.GetRowsAsync(InstanceId, result.DatasetId!, BulkDatasetRowFilter.All, 0, 10);
        page!.Rows.Should().ContainSingle(r => r.StructuralIssue != null && r.StructuralIssue.Contains("Duplicate"));
    }

    [Fact]
    public async Task Ingest_ExceedingMaxRowCount_FailsCleanly()
    {
        var (store, fileStorage) = MakeStore(new BulkDatasetIngestOptions { MaxRowCount = 2 });
        var csv = string.Join('\n',
            Header,
            "NJF-001,Alice,25.00,SN-1,,",
            "NJF-002,Bob,25.00,SN-2,,",
            "NJF-003,Cara,25.00,SN-3,,");
        var file = await SaveCsvAsync(fileStorage, csv);

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("maximum");
    }

    [Fact]
    public async Task Ingest_ExceedingMaxCellLength_FailsCleanly()
    {
        var (store, fileStorage) = MakeStore(new BulkDatasetIngestOptions { MaxCellLength = 5 });
        var csv = string.Join('\n', Header, "NJF-001,A very long name indeed,25.00,SN-1,,");
        var file = await SaveCsvAsync(fileStorage, csv);

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("maximum cell length");
    }

    [Fact]
    public async Task GetRows_NeedsAttentionFilter_OnlyReturnsErrorOrWarningRows_Paginated()
    {
        var (store, fileStorage) = MakeStore();
        var csv = string.Join('\n',
            Header,
            "NJF-001,Alice,25.00,SN-1,,",
            "NJF-002,Bob,25.00,SN-2,Bad row,",
            "NJF-003,Cara,25.00,SN-3,,A warning");
        var file = await SaveCsvAsync(fileStorage, csv);
        var result = await store.IngestAsync(InstanceId, file, Columns);

        var page = await store.GetRowsAsync(InstanceId, result.DatasetId!, BulkDatasetRowFilter.NeedsAttention, 0, 1);

        page!.TotalMatchingRowCount.Should().Be(2);
        page.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetSummary_UnknownDatasetId_ReturnsNull()
    {
        var (store, _) = MakeStore();

        var summary = await store.GetSummaryAsync(InstanceId, "not-a-real-id");

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetSummary_DatasetOwnedByAnotherInstance_ThrowsUnauthorized()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n', Header, "NJF-001,Alice,25.00,SN-1,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);

        var act = () => store.GetSummaryAsync("some-other-instance", result.DatasetId!);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ApplyCorrection_UpdatesCurrentValues_KeepsOriginalValuesImmutable()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n', Header, "NJF-001,Alice,25.00,SN-1,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);

        await store.ApplyCorrectionAsync(
            InstanceId, result.DatasetId!, "NJF-001",
            new Dictionary<string, string?> { ["memberName"] = "Alice Corrected" }, "test-user");

        var page = await store.GetRowsAsync(InstanceId, result.DatasetId!, BulkDatasetRowFilter.All, 0, 10);
        var row = page!.Rows.Single();
        row.CurrentValues["memberName"].Should().Be("Alice Corrected");
        row.OriginalValues["memberName"].Should().Be("Alice");
        row.Corrections.Should().ContainSingle(c =>
            c.ColumnKey == "memberName" && c.PreviousValue == "Alice" && c.NewValue == "Alice Corrected" && c.CorrectedBy == "test-user");
    }

    [Fact]
    public async Task ApplyCorrection_ToNonEditableColumn_Throws()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n', Header, "NJF-001,Alice,25.00,SN-1,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);

        var act = () => store.ApplyCorrectionAsync(
            InstanceId, result.DatasetId!, "NJF-001",
            new Dictionary<string, string?> { ["errorText"] = "sneaky" }, "test-user");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ApplyCorrection_UnknownRowKey_Throws()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n', Header, "NJF-001,Alice,25.00,SN-1,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);

        var act = () => store.ApplyCorrectionAsync(
            InstanceId, result.DatasetId!, "not-a-real-row",
            new Dictionary<string, string?> { ["memberName"] = "X" }, "test-user");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Materialize_RoundTrips_OriginalAndCorrectedValues_InIngestedColumnOrder()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n',
            Header,
            "NJF-001,Alice,25.00,SN-1,,",
            "NJF-002,Bob,30.00,SN-2,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);
        await store.ApplyCorrectionAsync(
            InstanceId, result.DatasetId!, "NJF-002",
            new Dictionary<string, string?> { ["memberName"] = "Robert" }, "test-user");

        var materialized = await store.MaterializeAsync(
            InstanceId, result.DatasetId!, "contributionsFile", "contributions.csv", sanitizeForHumanExport: false);

        var stream = await fileStorage.OpenReadAsync(materialized.StorageKey);
        using var text = new StreamReader(stream!);
        var content = await text.ReadToEndAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines[0].Should().Be(Header);
        lines.Should().Contain("NJF-001,Alice,25.00,SN-1,,");
        lines.Should().Contain("NJF-002,Robert,30.00,SN-2,,");
    }

    [Fact]
    public async Task Materialize_SanitizesFormulaInjection_OnlyForHumanExport()
    {
        var (store, fileStorage) = MakeStore();
        var file = await SaveCsvAsync(fileStorage, string.Join('\n', Header, "NJF-001,=cmd(dangerous),25.00,SN-1,,"));
        var result = await store.IngestAsync(InstanceId, file, Columns);

        var humanExport = await store.MaterializeAsync(
            InstanceId, result.DatasetId!, "downloadFile", "download.csv", sanitizeForHumanExport: true);
        var machineExport = await store.MaterializeAsync(
            InstanceId, result.DatasetId!, "contributionsFile", "contributions.csv", sanitizeForHumanExport: false);

        var humanContent = await ReadAllAsync(fileStorage, humanExport.StorageKey);
        var machineContent = await ReadAllAsync(fileStorage, machineExport.StorageKey);

        humanContent.Should().Contain("'=cmd(dangerous)");
        machineContent.Should().Contain("=cmd(dangerous)");
        machineContent.Should().NotContain("'=cmd(dangerous)");
    }

    private static async Task<string> ReadAllAsync(InMemoryServiceRequestFileStorage fileStorage, string storageKey)
    {
        var stream = await fileStorage.OpenReadAsync(storageKey);
        using var reader = new StreamReader(stream!);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Ingest_ManyRows_CompletesAndCountsCorrectly()
    {
        var (store, fileStorage) = MakeStore();
        var sb = new StringBuilder(Header);
        const int rowCount = 10_000;
        for (var i = 0; i < rowCount; i++)
        {
            sb.Append('\n').Append($"NJF-{i:D6},Member {i},25.00,SN-{i},,");
        }

        var file = await SaveCsvAsync(fileStorage, sb.ToString());

        var result = await store.IngestAsync(InstanceId, file, Columns);

        result.Succeeded.Should().BeTrue();
        result.Summary!.TotalRowCount.Should().Be(rowCount);
        result.Summary.AcceptedRowCount.Should().Be(rowCount);
    }
}
