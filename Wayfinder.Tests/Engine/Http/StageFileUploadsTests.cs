using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Wayfinder.Engine.Http;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Http;

/// <summary>
/// <see cref="StageFileUploads.ApplyFileUploadsAsync"/> — first direct unit coverage (moved here
/// from Wayfinder.ReferenceApp/Program.cs, where it was only ever reachable indirectly via a full
/// Playwright run). Uses the real <see cref="InMemoryServiceRequestFileStorage"/>, not a mock —
/// no behaviour here is worth faking out.
/// </summary>
public class StageFileUploadsTests
{
    private const string InstanceId = "instance-1";

    private static IFormFile MakeFile(string fieldKey, string fileName, string content, string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, $"field:{fieldKey}", fileName) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    private static IFormCollection FormWith(params IFormFile[] files)
    {
        var fileCollection = new FormFileCollection();
        fileCollection.AddRange(files);
        return new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), fileCollection);
    }

    private static StepContent StepWithFileUploadField(string fieldKey, long? maxSizeBytes = null, IReadOnlyList<string>? acceptedFileTypes = null) => new()
    {
        StepType = "question",
        StateDisplayName = "Upload",
        AvailableActions = [],
        Components =
        [
            new ComponentRenderPayload
            {
                Type = "fieldset",
                Fields = [new FieldRenderPayload
                {
                    FieldKey = fieldKey, Label = "Evidence", FieldType = "file-upload", Required = false,
                    MaxSizeBytes = maxSizeBytes, AcceptedFileTypes = acceptedFileTypes
                }]
            }
        ]
    };

    [Fact]
    public async Task AcceptedFile_IsSaved_AndWritesARealReferenceIntoFieldValues()
    {
        var storage = new InMemoryServiceRequestFileStorage();
        var form = FormWith(MakeFile("evidence", "notes.txt", "hello world"));
        var fieldValues = new Dictionary<string, object?>();

        var problems = await StageFileUploads.ApplyFileUploadsAsync(form, StepWithFileUploadField("evidence"), InstanceId, storage, fieldValues);

        problems.Should().BeEmpty();
        fieldValues.Should().ContainKey("evidence");
        var reference = fieldValues["evidence"].Should().BeOfType<ServiceRequestFileReference>().Subject;
        reference.OriginalFileName.Should().Be("notes.txt");
        reference.SizeBytes.Should().Be(11);
        (await storage.OpenReadAsync(reference.StorageKey)).Should().NotBeNull("the file must genuinely be retrievable, not just referenced");
    }

    [Fact]
    public async Task OversizedFile_IsRejected_AndFieldValuesIsLeftUntouched()
    {
        var storage = new InMemoryServiceRequestFileStorage();
        var form = FormWith(MakeFile("evidence", "big.txt", new string('x', 100)));
        var fieldValues = new Dictionary<string, object?>();

        var problems = await StageFileUploads.ApplyFileUploadsAsync(
            form, StepWithFileUploadField("evidence", maxSizeBytes: 10), InstanceId, storage, fieldValues);

        problems.Should().ContainSingle(p => p.FieldKey == "evidence" && p.Code == "VALIDATION_ERROR");
        fieldValues.Should().NotContainKey("evidence");
    }

    [Fact]
    public async Task DisallowedFileType_IsRejected_AndFieldValuesIsLeftUntouched()
    {
        var storage = new InMemoryServiceRequestFileStorage();
        var form = FormWith(MakeFile("evidence", "notes.exe", "hello"));
        var fieldValues = new Dictionary<string, object?>();

        var problems = await StageFileUploads.ApplyFileUploadsAsync(
            form, StepWithFileUploadField("evidence", acceptedFileTypes: [".pdf", ".jpg"]), InstanceId, storage, fieldValues);

        problems.Should().ContainSingle(p => p.FieldKey == "evidence" && p.Code == "VALIDATION_ERROR");
        fieldValues.Should().NotContainKey("evidence");
    }

    [Fact]
    public async Task NoFilePosted_LeavesTheFieldKeyAbsentEntirely_SoAnExistingReferenceSurvivesTheMerge()
    {
        var storage = new InMemoryServiceRequestFileStorage();
        var form = FormWith(); // nothing posted this round
        var fieldValues = new Dictionary<string, object?>();

        var problems = await StageFileUploads.ApplyFileUploadsAsync(form, StepWithFileUploadField("evidence"), InstanceId, storage, fieldValues);

        problems.Should().BeEmpty();
        fieldValues.Should().NotContainKey("evidence", "an absent key lets the engine's own Merge preserve whatever's already stored");
    }

    [Fact]
    public async Task NullRender_ReturnsNoProblems_TouchesNothing()
    {
        var storage = new InMemoryServiceRequestFileStorage();
        var fieldValues = new Dictionary<string, object?>();

        var problems = await StageFileUploads.ApplyFileUploadsAsync(FormWith(), null, InstanceId, storage, fieldValues);

        problems.Should().BeEmpty();
        fieldValues.Should().BeEmpty();
    }
}
