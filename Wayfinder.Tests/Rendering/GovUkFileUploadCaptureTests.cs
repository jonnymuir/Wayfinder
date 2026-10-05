using FluentAssertions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// A <c>file-upload</c> field's capture mode decides whether a phone opens the camera directly or
/// offers both the camera and existing files. Unset keeps the plain file input.
/// </summary>
public class GovUkFileUploadCaptureTests
{
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    private static string Render(string? captureMode) => GovUkFields.Render(new FieldRenderPayload
    {
        FieldKey = "sightingPhoto",
        Label = "Photo of the butterfly",
        FieldType = "file-upload",
        Required = false,
        AcceptedFileTypes = [".jpg", ".png"],
        CaptureMode = captureMode,
    }, NoErrors);

    [Fact]
    public void CameraMode_OpensTheRearCameraDirectly()
    {
        Render("camera").Should().Contain("capture=\"environment\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("choose")]
    public void ChooseOrUnset_LeavesThePhoneToOfferTheCameraAndExistingFiles(string? mode)
    {
        Render(mode).Should().NotContain("capture=");
    }

    [Fact]
    public void CameraMode_KeepsTheAcceptedFileTypes()
    {
        Render("camera").Should().Contain("accept=\".jpg,.png\"");
    }

    [Fact]
    public void TheCaptureAttributeHelper_MatchesWhatTheRendererEmits()
    {
        GovUkFileUploadField.CaptureAttribute("camera").Should().Be(" capture=\"environment\"");
        GovUkFileUploadField.CaptureAttribute("choose").Should().BeEmpty();
        GovUkFileUploadField.CaptureAttribute(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("camera", 0)]
    [InlineData("choose", 0)]
    [InlineData("selfie", 1)]
    public void ABlueprintDesigner_CanOnlyPickAKnownCaptureMode(string mode, int expectedDiagnostics)
    {
        var descriptor = BuiltInComponentDescriptors.All.Single(d => d.Discriminator == "file-upload");
        var component = new FileUploadComponent { FieldKey = "photo", Label = "Photo", CaptureMode = mode };

        ComponentPropertyValidator.Validate(component, descriptor, "stage/photo").Should().HaveCount(expectedDiagnostics);
    }
}
