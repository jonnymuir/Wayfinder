using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// An image file input is marked so a script can show the visitor what they chose: the control a browser draws
/// for a chosen file is not a reliable preview (a phone's webview can draw it black). Only image inputs are marked.
/// </summary>
public class GovUkFileUploadPreviewTests
{
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    private static string Render(params string[] acceptedFileTypes) => GovUkFields.Render(new FieldRenderPayload
    {
        FieldKey = "photo",
        Label = "Photo",
        FieldType = "file-upload",
        Required = false,
        AcceptedFileTypes = acceptedFileTypes.Length == 0 ? null : acceptedFileTypes,
    }, NoErrors);

    [Theory]
    [InlineData(".jpg", ".png")]
    [InlineData(".JPEG")]
    [InlineData(".pdf", ".webp")]
    [InlineData(" .heic ")]
    public void AnInputThatAcceptsAnImage_IsMarkedForPreview(params string[] accepted)
    {
        Render(accepted).Should().Contain("data-wayfinder-file-preview");
    }

    [Theory]
    [InlineData(".pdf", ".docx")]
    [InlineData(".csv")]
    public void AnInputThatAcceptsNoImage_IsNotMarked(params string[] accepted)
    {
        Render(accepted).Should().NotContain("data-wayfinder-file-preview");
    }

    [Fact]
    public void AnInputWithNoAcceptedTypes_IsNotMarked_BecauseItCouldBeAnything()
    {
        Render().Should().NotContain("data-wayfinder-file-preview");
    }

    [Fact]
    public void TheHostRule_MatchesWhatTheRendererMarks()
    {
        GovUkFileUploadField.AcceptsImages([".jpg"]).Should().BeTrue();
        GovUkFileUploadField.AcceptsImages([".pdf"]).Should().BeFalse();
        GovUkFileUploadField.AcceptsImages(null).Should().BeFalse();
        GovUkFileUploadField.AcceptsImages([]).Should().BeFalse();
        GovUkFileUploadField.PreviewAttribute([".png"]).Should().Be(" data-wayfinder-file-preview");
        GovUkFileUploadField.PreviewAttribute([".pdf"]).Should().BeEmpty();
    }

    [Fact]
    public void ThePreviewMarkerSitsOnTheFileInput_AlongsideItsOtherAttributes()
    {
        var html = Render(".jpg");

        html.Should().MatchRegex("""<input[^>]*type="file"[^>]*accept="\.jpg"[^>]*data-wayfinder-file-preview""");
    }
}
