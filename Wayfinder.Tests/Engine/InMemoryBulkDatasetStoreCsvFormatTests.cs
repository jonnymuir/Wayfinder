using System.Globalization;
using System.Text;
using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.BulkData;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// The CSV dialect <see cref="InMemoryBulkDatasetStore"/> reads and writes: RFC 4180, comma
/// separated, header matched exactly, values kept as written. These pin what an uploaded
/// spreadsheet export turns into, and what a materialized file looks like byte for byte, so a
/// change of CSV library can't quietly change either.
/// </summary>
public class InMemoryBulkDatasetStoreCsvFormatTests
{
    private const string InstanceId = "instance-1";
    private const string Header = "ref,name,note";

    private static readonly IReadOnlyList<BulkDatasetColumnDescriptor> Columns =
    [
        new() { Key = "ref", Title = "Ref", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.RowKey },
        new() { Key = "name", Title = "Name", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.Data, Editable = true },
        new() { Key = "note", Title = "Note", ValueKind = ComponentPropertyValueKind.String, Role = BulkDatasetColumnRole.Data, Editable = true },
    ];

    private static async Task<(InMemoryBulkDatasetStore Store, InMemoryServiceRequestFileStorage Files, BulkDatasetIngestResult Result)> IngestAsync(byte[] content)
    {
        var files = new InMemoryServiceRequestFileStorage();
        var store = new InMemoryBulkDatasetStore(files);
        await using var stream = new MemoryStream(content);
        var key = await files.SaveAsync(InstanceId, "file", stream, "upload.csv");
        var file = new ServiceRequestFileReference
        {
            StorageKey = key,
            OriginalFileName = "upload.csv",
            ContentType = "text/csv",
            SizeBytes = content.Length,
        };
        return (store, files, await store.IngestAsync(InstanceId, file, Columns));
    }

    private static Task<(InMemoryBulkDatasetStore Store, InMemoryServiceRequestFileStorage Files, BulkDatasetIngestResult Result)> IngestAsync(string content) =>
        IngestAsync(Encoding.UTF8.GetBytes(content));

    private static async Task<IReadOnlyList<BulkDatasetRow>> RowsAsync(InMemoryBulkDatasetStore store, BulkDatasetIngestResult result)
    {
        result.Succeeded.Should().BeTrue(result.FailureReason);
        var page = await store.GetRowsAsync(InstanceId, result.DatasetId!, BulkDatasetRowFilter.All, 0, 100);
        return page!.Rows;
    }

    private static async Task<string> MaterializeAsync(
        InMemoryBulkDatasetStore store, InMemoryServiceRequestFileStorage files, BulkDatasetIngestResult result)
    {
        var materialized = await store.MaterializeAsync(InstanceId, result.DatasetId!, "out", "out.csv", sanitizeForHumanExport: false);
        await using var stream = await files.OpenReadAsync(materialized.StorageKey);
        using var memory = new MemoryStream();
        await stream!.CopyToAsync(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    [Fact]
    public async Task QuotedFields_KeepTheirCommasQuotesAndLineBreaks()
    {
        var (store, _, result) = await IngestAsync(
            Header + "\n" +
            "A1,\"Smith, Jo\",\"She said \"\"hi\"\"\"\n" +
            "A2,Lee,\"line one\nline two\"\n");

        var rows = await RowsAsync(store, result);

        rows.Should().HaveCount(2);
        rows[0].OriginalValues["name"].Should().Be("Smith, Jo");
        rows[0].OriginalValues["note"].Should().Be("She said \"hi\"");
        rows[1].OriginalValues["note"].Should().Be("line one\nline two");
    }

    [Fact]
    public async Task AnExcelExport_WithAByteOrderMarkAndWindowsLineEndings_IsReadNormally()
    {
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Header + "\r\nA1,Ana,ok\r\nA2,Ben,\r\n")).ToArray();

        var (store, _, result) = await IngestAsync(content);

        var rows = await RowsAsync(store, result);
        rows.Select(r => r.RowKey).Should().Equal("A1", "A2");
        rows[0].OriginalValues["note"].Should().Be("ok");
        rows[1].OriginalValues["note"].Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyCommasSeparateFields_ASemicolonFileIsMissingItsColumns()
    {
        var (_, _, result) = await IngestAsync("ref;name;note\nA1;Ana;ok\nA2;Ben;fine\n");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("missing expected column");
    }

    [Fact]
    public async Task SemicolonsInsideValues_StayInTheValue()
    {
        var (store, _, result) = await IngestAsync(Header + "\nA1;x,Ana;Bo;Cy,a;b;c\nA2;y,Dee;Ed;Flo,d;e;f\n");

        var rows = await RowsAsync(store, result);
        rows[0].RowKey.Should().Be("A1;x");
        rows[0].OriginalValues["name"].Should().Be("Ana;Bo;Cy");
    }

    [Fact]
    public async Task HeaderNames_MatchExactly()
    {
        var (_, _, result) = await IngestAsync("Ref,Name,Note\nA1,Ana,ok\n");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("ref").And.Contain("name").And.Contain("note");
    }

    [Fact]
    public async Task ColumnsAreFoundByName_InAnyOrder_AndExtraColumnsAreIgnored()
    {
        var (store, _, result) = await IngestAsync("extra,note,name,ref\nzzz,ok,Ana,A1\n");

        var rows = await RowsAsync(store, result);
        rows.Should().ContainSingle();
        rows[0].RowKey.Should().Be("A1");
        rows[0].OriginalValues.Should().Equal(new Dictionary<string, string?> { ["ref"] = "A1", ["name"] = "Ana", ["note"] = "ok" });
    }

    [Fact]
    public async Task AShortRow_ReadsItsMissingTrailingFieldsAsAbsent()
    {
        var (store, _, result) = await IngestAsync(Header + "\nA1,Ana\nA2\n");

        var rows = await RowsAsync(store, result);
        rows[0].OriginalValues["name"].Should().Be("Ana");
        rows[0].OriginalValues["note"].Should().BeNull();
        rows[1].OriginalValues["name"].Should().BeNull();
    }

    [Fact]
    public async Task ValuesAreKeptAsWritten_SpacesAndAStrayQuoteIncluded()
    {
        var (store, _, result) = await IngestAsync(Header + "\nA1,  Ana  ,Al\"ice\n");

        var rows = await RowsAsync(store, result);
        rows[0].OriginalValues["name"].Should().Be("  Ana  ");
        rows[0].OriginalValues["note"].Should().Be("Al\"ice");
    }

    [Fact]
    public async Task BlankLinesAreSkipped()
    {
        var (store, _, result) = await IngestAsync(Header + "\n\nA1,Ana,ok\n\n\nA2,Ben,ok\n\n");

        var rows = await RowsAsync(store, result);
        rows.Select(r => r.RowKey).Should().Equal("A1", "A2");
    }

    [Fact]
    public async Task AHeaderWithNoRows_IsAnEmptyDataset_NotAnEmptyFile()
    {
        var (store, _, result) = await IngestAsync(Header + "\n");

        result.Succeeded.Should().BeTrue();
        (await RowsAsync(store, result)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARepeatedHeaderName_ReadsTheFirstColumn()
    {
        var (store, _, result) = await IngestAsync("ref,name,note,name\nA1,first,ok,second\n");

        var rows = await RowsAsync(store, result);
        rows[0].OriginalValues["name"].Should().Be("first");
    }

    [Fact]
    public async Task Materialize_WritesRfc4180_WithWindowsLineEndings_QuotingOnlyWhatNeedsIt()
    {
        var (store, files, result) = await IngestAsync(
            Header + "\n" +
            "A1,\"Smith, Jo\",\"She said \"\"hi\"\"\"\n" +
            "A2,\" padded \",\"two\nlines\"\n" +
            "A3,plain,\n");

        var content = await MaterializeAsync(store, files, result);

        content.Should().Be(
            "ref,name,note\r\n" +
            "A1,\"Smith, Jo\",\"She said \"\"hi\"\"\"\r\n" +
            "A2,\" padded \",\"two\nlines\"\r\n" +
            "A3,plain,\r\n");
    }

    [Fact]
    public async Task Materialize_WritesAnAbsentValueAsAnEmptyField()
    {
        var (store, files, result) = await IngestAsync(Header + "\nA1,Ana\n");

        var content = await MaterializeAsync(store, files, result);

        content.Should().Be("ref,name,note\r\nA1,Ana,\r\n");
    }

    [Fact]
    public async Task Materialize_ThenIngest_RoundTripsEveryValueExactly()
    {
        var awkward = new[] { "Smith, Jo", "She said \"hi\"", "two\r\nlines", " padded ", "=SUM(A1)", "café ✓", "" };
        var source = new StringBuilder(Header + "\n");
        for (var i = 0; i < awkward.Length; i++)
        {
            source.Append(CultureInfo.InvariantCulture, $"K{i},\"{awkward[i].Replace("\"", "\"\"")}\",n{i}\n");
        }

        var (store, files, first) = await IngestAsync(source.ToString());
        var materialized = await MaterializeAsync(store, files, first);
        var (secondStore, _, second) = await IngestAsync(materialized);

        var rows = await RowsAsync(secondStore, second);
        rows.Select(r => r.OriginalValues["name"]).Should().Equal(awkward);
    }
}
