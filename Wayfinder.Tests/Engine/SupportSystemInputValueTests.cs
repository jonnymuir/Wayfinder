using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// A support-system capability input resolved from a blueprint field must correctly tell a
/// file-upload field's value apart from an ordinary scalar — a client reads file bytes itself via
/// its own host-registered file storage, so getting this wrong would leave it unable to find the
/// file at all.
/// </summary>
public class SupportSystemInputValueTests
{
    [Fact]
    public void Resolve_FileUploadFieldValue_PopulatesFileReference()
    {
        var reference = new ServiceRequestFileReference
        {
            StorageKey = "memory://abc123",
            OriginalFileName = "risk-assessment.pdf",
            ContentType = "application/pdf",
            SizeBytes = 4096
        };

        var resolved = SupportSystemInputValue.Resolve(reference);

        resolved.FileReference.Should().Be(reference);
    }

    [Fact]
    public void Resolve_PlainScalarValue_PopulatesRawValueOnly()
    {
        var resolved = SupportSystemInputValue.Resolve("some notes");

        resolved.RawValue.Should().Be("some notes");
        resolved.FileReference.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullValue_PopulatesNeither()
    {
        var resolved = SupportSystemInputValue.Resolve(null);

        resolved.RawValue.Should().BeNull();
        resolved.FileReference.Should().BeNull();
    }
}
