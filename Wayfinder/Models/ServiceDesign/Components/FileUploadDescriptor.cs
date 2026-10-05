namespace Wayfinder.Models.ServiceDesign.Components;

internal static class FileUploadDescriptor
{
    internal static ComponentDescriptor Build() => new()
    {
        Discriminator = "file-upload", DisplayName = "File upload", Category = ComponentCategory.Input,
        Description = "A single named document slot — one component per document a blueprint needs.",
        ClrType = typeof(FileUploadComponent), IsInput = true,
        Properties =
        [
            .. BuiltInComponentDescriptors.InputBaseProperties(),
            BuiltInComponentDescriptors.Prop(nameof(FileUploadComponent.AcceptedFileTypes), "Accepted file types",
                ComponentPropertyValueKind.StringArray, "e.g. [\".pdf\", \".jpg\", \".png\"]. Omit for no restriction."),
            BuiltInComponentDescriptors.Prop(nameof(FileUploadComponent.MaxSizeBytes), "Maximum size (bytes)",
                ComponentPropertyValueKind.Integer, "Falls back to the platform's own default limit if omitted."),
            BuiltInComponentDescriptors.Prop(nameof(FileUploadComponent.CaptureMode), "Capture mode",
                ComponentPropertyValueKind.String,
                "choose (default): the phone offers the camera and existing files. camera: opens the rear camera directly.",
                editor: "select", allowedValues: ["choose", "camera"]),
        ],
    };
}
