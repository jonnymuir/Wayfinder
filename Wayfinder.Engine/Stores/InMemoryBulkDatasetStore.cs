using System.Collections.Concurrent;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Engine.Stores;

/// <summary>
/// Host-configurable ingest caps — protection against an oversized or adversarial file, checked
/// incrementally during streaming, never after a full parse (see docs/guides/bulk-data-review.md's
/// performance principles). Exceeding either aborts ingest with a normal
/// <see cref="BulkDatasetIngestResult.Failure"/>, not an exception.
/// </summary>
public sealed record BulkDatasetIngestOptions
{
    public int MaxRowCount { get; init; } = 100_000;
    public int MaxCellLength { get; init; } = 10_000;
}

/// <summary>
/// Default <see cref="IBulkDatasetStore"/> — process-lifetime only, matching this toolkit's other
/// in-memory defaults (<see cref="InMemoryServiceRequestFileStorage"/>,
/// <see cref="InMemoryServiceRequestStore"/>). A real host backs this with an indexed table
/// instead (see the type's own remarks on <see cref="IBulkDatasetStore"/> and
/// docs/guides/bulk-data-review.md's "no OLAP engine, ordinary paged CRUD" guidance); nothing
/// here survives a restart.
/// </summary>
public sealed class InMemoryBulkDatasetStore : IBulkDatasetStore
{
    private readonly IServiceRequestFileStorage _fileStorage;
    private readonly BulkDatasetIngestOptions _options;
    private readonly ConcurrentDictionary<string, BulkDataset> _datasets = new(StringComparer.Ordinal);

    public InMemoryBulkDatasetStore(IServiceRequestFileStorage fileStorage, BulkDatasetIngestOptions? options = null)
    {
        _fileStorage = fileStorage;
        _options = options ?? new BulkDatasetIngestOptions();
    }

    public async Task<BulkDatasetIngestResult> IngestAsync(
        string instanceId,
        ServiceRequestFileReference sourceFile,
        IReadOnlyList<BulkDatasetColumnDescriptor> columns,
        CancellationToken ct = default)
    {
        var rowKeyColumn = columns.FirstOrDefault(c => c.Role == BulkDatasetColumnRole.RowKey);
        if (rowKeyColumn is null)
        {
            return BulkDatasetIngestResult.Failure("No column declares role RowKey.");
        }

        await using var fileStream = await _fileStorage.OpenReadAsync(sourceFile.StorageKey, ct);
        if (fileStream is null)
        {
            return BulkDatasetIngestResult.Failure($"Source file '{sourceFile.StorageKey}' could not be opened.");
        }

        using var reader = new StreamReader(fileStream);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            BadDataFound = null,
        };
        using var csv = new CsvReader(reader, csvConfig);

        if (!await csv.ReadAsync())
        {
            return BulkDatasetIngestResult.Failure("File is empty.");
        }

        csv.ReadHeader();
        var header = new HashSet<string>(csv.HeaderRecord ?? [], StringComparer.Ordinal);
        var missingColumns = columns.Where(c => !header.Contains(c.Key)).Select(c => c.Key).ToArray();
        if (missingColumns.Length > 0)
        {
            return BulkDatasetIngestResult.Failure(
                $"File is missing expected column(s): {string.Join(", ", missingColumns)}.");
        }

        var rows = new List<MutableRow>();
        var rowsByKey = new Dictionary<string, MutableRow>(StringComparer.Ordinal);
        var rowIndex = 0;

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (rowIndex >= _options.MaxRowCount)
            {
                return BulkDatasetIngestResult.Failure(
                    $"File exceeds the maximum of {_options.MaxRowCount} rows.");
            }

            var originalValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                csv.TryGetField<string>(column.Key, out var value);
                if (value is { Length: > 0 } && value.Length > _options.MaxCellLength)
                {
                    return BulkDatasetIngestResult.Failure(
                        $"Row {rowIndex + 1}, column '{column.Key}' exceeds the maximum cell length of " +
                        $"{_options.MaxCellLength} characters.");
                }

                originalValues[column.Key] = value;
            }

            var rawRowKey = originalValues.GetValueOrDefault(rowKeyColumn.Key);
            string? structuralIssue = null;
            var rowKey = rawRowKey;
            if (string.IsNullOrWhiteSpace(rawRowKey))
            {
                rowKey = $"__row{rowIndex}";
                structuralIssue = $"Missing value for row key column '{rowKeyColumn.Key}'.";
            }
            else if (rowsByKey.ContainsKey(rawRowKey))
            {
                rowKey = $"__row{rowIndex}";
                structuralIssue = $"Duplicate row key '{rawRowKey}'.";
            }

            var hasError = structuralIssue is not null || columns.Any(c =>
                c.Role == BulkDatasetColumnRole.ResponseError &&
                !string.IsNullOrEmpty(originalValues.GetValueOrDefault(c.Key)));
            var hasWarning = columns.Any(c =>
                c.Role == BulkDatasetColumnRole.ResponseWarning &&
                !string.IsNullOrEmpty(originalValues.GetValueOrDefault(c.Key)));

            var row = new MutableRow
            {
                RowKey = rowKey!,
                RowIndex = rowIndex,
                OriginalValues = originalValues,
                CurrentValues = new Dictionary<string, string?>(originalValues, StringComparer.Ordinal),
                HasError = hasError,
                HasWarning = hasWarning,
                StructuralIssue = structuralIssue,
            };
            rows.Add(row);
            rowsByKey[row.RowKey] = row;
            rowIndex++;
        }

        var datasetId = Guid.NewGuid().ToString("N");
        var dataset = new BulkDataset
        {
            InstanceId = instanceId,
            Columns = columns,
            Rows = rows,
            RowsByKey = rowsByKey,
        };
        _datasets[datasetId] = dataset;

        return BulkDatasetIngestResult.Success(Summarize(datasetId, dataset));
    }

    public Task<BulkDatasetSummary?> GetSummaryAsync(string instanceId, string datasetId, CancellationToken ct = default)
    {
        var dataset = GetOwnedDataset(instanceId, datasetId);
        if (dataset is null)
        {
            return Task.FromResult<BulkDatasetSummary?>(null);
        }

        lock (dataset)
        {
            return Task.FromResult<BulkDatasetSummary?>(Summarize(datasetId, dataset));
        }
    }

    public Task<BulkDatasetRowPage?> GetRowsAsync(
        string instanceId,
        string datasetId,
        BulkDatasetRowFilter filter,
        int pageIndex,
        int pageSize,
        CancellationToken ct = default)
    {
        var dataset = GetOwnedDataset(instanceId, datasetId);
        if (dataset is null)
        {
            return Task.FromResult<BulkDatasetRowPage?>(null);
        }

        lock (dataset)
        {
            var matching = dataset.Rows.Where(row => Matches(row, filter)).ToList();
            var page = matching.Skip(pageIndex * pageSize).Take(pageSize).Select(ToRow).ToArray();
            return Task.FromResult<BulkDatasetRowPage?>(new BulkDatasetRowPage
            {
                Rows = page,
                PageIndex = pageIndex,
                PageSize = pageSize,
                TotalMatchingRowCount = matching.Count,
            });
        }
    }

    public Task ApplyCorrectionAsync(
        string instanceId,
        string datasetId,
        string rowKey,
        IReadOnlyDictionary<string, string?> correctedValues,
        string correctedBy,
        CancellationToken ct = default)
    {
        var dataset = GetOwnedDataset(instanceId, datasetId)
            ?? throw new KeyNotFoundException($"Dataset '{datasetId}' not found.");

        var editableKeys = dataset.Columns
            .Where(c => c.Role == BulkDatasetColumnRole.Data && c.Editable)
            .Select(c => c.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var columnKey in correctedValues.Keys)
        {
            if (!editableKeys.Contains(columnKey))
            {
                throw new ArgumentException(
                    $"Column '{columnKey}' is not an editable Data column on this dataset.",
                    nameof(correctedValues));
            }
        }

        lock (dataset)
        {
            if (!dataset.RowsByKey.TryGetValue(rowKey, out var row))
            {
                throw new KeyNotFoundException($"Row '{rowKey}' not found in dataset '{datasetId}'.");
            }

            var correctedAt = DateTimeOffset.UtcNow;
            foreach (var (columnKey, newValue) in correctedValues)
            {
                var previousValue = row.CurrentValues.GetValueOrDefault(columnKey);
                row.Corrections.Add(new BulkDatasetRowCorrection
                {
                    ColumnKey = columnKey,
                    PreviousValue = previousValue,
                    NewValue = newValue,
                    CorrectedBy = correctedBy,
                    CorrectedAt = correctedAt,
                });
                row.CurrentValues[columnKey] = newValue;
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> RevertCorrectionsAsync(
        string instanceId,
        string datasetId,
        string revertedBy,
        CancellationToken ct = default)
    {
        var dataset = GetOwnedDataset(instanceId, datasetId)
            ?? throw new KeyNotFoundException($"Dataset '{datasetId}' not found.");

        var revertedAt = DateTimeOffset.UtcNow;
        var revertedCount = 0;

        lock (dataset)
        {
            foreach (var row in dataset.Rows)
            {
                if (!IsDirty(row))
                {
                    continue;
                }

                foreach (var (columnKey, originalValue) in row.OriginalValues)
                {
                    var currentValue = row.CurrentValues.GetValueOrDefault(columnKey);
                    if (string.Equals(currentValue, originalValue, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    row.Corrections.Add(new BulkDatasetRowCorrection
                    {
                        ColumnKey = columnKey,
                        PreviousValue = currentValue,
                        NewValue = originalValue,
                        CorrectedBy = revertedBy,
                        CorrectedAt = revertedAt,
                    });
                    row.CurrentValues[columnKey] = originalValue;
                }

                revertedCount++;
            }
        }

        return Task.FromResult(revertedCount);
    }

    public async Task<ServiceRequestFileReference> MaterializeAsync(
        string instanceId,
        string datasetId,
        string targetFieldKey,
        string fileName,
        bool sanitizeForHumanExport,
        CancellationToken ct = default)
    {
        var dataset = GetOwnedDataset(instanceId, datasetId)
            ?? throw new KeyNotFoundException($"Dataset '{datasetId}' not found.");

        IReadOnlyList<BulkDatasetColumnDescriptor> columns;
        List<MutableRow> rowsSnapshot;
        lock (dataset)
        {
            columns = dataset.Columns;
            rowsSnapshot = [.. dataset.Rows];
        }

        using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, leaveOpen: true))
        await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var column in columns)
            {
                csv.WriteField(column.Key);
            }

            await csv.NextRecordAsync();

            foreach (var row in rowsSnapshot)
            {
                foreach (var column in columns)
                {
                    var value = row.CurrentValues.GetValueOrDefault(column.Key);
                    csv.WriteField(sanitizeForHumanExport ? SanitizeForSpreadsheetExport(value) : value);
                }

                await csv.NextRecordAsync();
            }
        }

        buffer.Position = 0;
        var storageKey = await _fileStorage.SaveAsync(instanceId, targetFieldKey, buffer, fileName, ct);

        return new ServiceRequestFileReference
        {
            StorageKey = storageKey,
            OriginalFileName = fileName,
            ContentType = "text/csv",
            SizeBytes = buffer.Length,
        };
    }

    /// <summary>
    /// OWASP's standard CSV-formula-injection mitigation: a cell a spreadsheet would otherwise
    /// interpret as a formula gets a leading single quote, forcing it to render as text. Only
    /// ever applied to a human-facing export — see <see cref="MaterializeAsync"/>'s own remarks
    /// for why the resubmission path must stay byte-faithful instead.
    /// </summary>
    private static string? SanitizeForSpreadsheetExport(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    private BulkDataset? GetOwnedDataset(string instanceId, string datasetId)
    {
        if (!_datasets.TryGetValue(datasetId, out var dataset))
        {
            return null;
        }

        if (!string.Equals(dataset.InstanceId, instanceId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Dataset '{datasetId}' does not belong to instance '{instanceId}'.");
        }

        return dataset;
    }

    private static BulkDatasetSummary Summarize(string datasetId, BulkDataset dataset)
    {
        var errorCount = dataset.Rows.Count(row => row.HasError);
        var warningCount = dataset.Rows.Count(row => row.HasWarning);
        var acceptedCount = dataset.Rows.Count(row => !row.HasError && !row.HasWarning);
        var dirtyCount = dataset.Rows.Count(IsDirty);

        return new BulkDatasetSummary
        {
            DatasetId = datasetId,
            TotalRowCount = dataset.Rows.Count,
            ErrorRowCount = errorCount,
            WarningRowCount = warningCount,
            AcceptedRowCount = acceptedCount,
            DirtyRowCount = dirtyCount,
            Columns = dataset.Columns,
        };
    }

    /// <summary>
    /// A value-diff, not a <c>Corrections.Count &gt; 0</c> check — see
    /// <see cref="BulkDatasetSummary.DirtyRowCount"/>'s own remarks for why. <c>CurrentValues</c>
    /// and <c>OriginalValues</c> always share the same key set (only declared editable columns are
    /// ever corrected, and those keys already exist from ingest), so comparing every
    /// <c>OriginalValues</c> entry against its current counterpart is exhaustive.
    /// </summary>
    private static bool IsDirty(MutableRow row) =>
        row.OriginalValues.Any(kv => !string.Equals(kv.Value, row.CurrentValues.GetValueOrDefault(kv.Key), StringComparison.Ordinal));

    private static bool Matches(MutableRow row, BulkDatasetRowFilter filter) => filter switch
    {
        BulkDatasetRowFilter.NeedsAttention => row.HasError || row.HasWarning,
        BulkDatasetRowFilter.HasError => row.HasError,
        BulkDatasetRowFilter.HasWarningOnly => row.HasWarning && !row.HasError,
        BulkDatasetRowFilter.Accepted => !row.HasError && !row.HasWarning,
        BulkDatasetRowFilter.All => true,
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static BulkDatasetRow ToRow(MutableRow row) => new()
    {
        RowKey = row.RowKey,
        RowIndex = row.RowIndex,
        OriginalValues = row.OriginalValues,
        CurrentValues = row.CurrentValues,
        Corrections = [.. row.Corrections],
        HasError = row.HasError,
        HasWarning = row.HasWarning,
        StructuralIssue = row.StructuralIssue,
    };

    private sealed class BulkDataset
    {
        public required string InstanceId { get; init; }
        public required IReadOnlyList<BulkDatasetColumnDescriptor> Columns { get; init; }
        public required List<MutableRow> Rows { get; init; }
        public required Dictionary<string, MutableRow> RowsByKey { get; init; }
    }

    private sealed class MutableRow
    {
        public required string RowKey { get; init; }
        public required int RowIndex { get; init; }
        public required Dictionary<string, string?> OriginalValues { get; init; }
        public required Dictionary<string, string?> CurrentValues { get; init; }
        public List<BulkDatasetRowCorrection> Corrections { get; } = [];
        public required bool HasError { get; init; }
        public required bool HasWarning { get; init; }
        public string? StructuralIssue { get; init; }
    }
}
