using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

public static class GovUkFileUploadField
{
    /// <summary>
    /// A plain, synchronous <c>govuk-file-upload</c> — posted as part of the normal form submit,
    /// with the host saving it and swapping the value for a reference before it reaches the
    /// engine (the engine itself never sees raw bytes). Deliberately not Wayfinder.Umbraco's
    /// async progressive-upload-with-token pattern — that needs its own JS runtime this package
    /// doesn't ship.
    /// </summary>
    internal static string Render(FieldRenderPayload field, IReadOnlyDictionary<string, string> errors)
    {
        var (id, name, hint, describedBy, required, error) = GovUkFields.Common(field, errors);
        var alreadyUploaded = !string.IsNullOrEmpty(field.Value?.ToString());
        var accept = field.AcceptedFileTypes is { Count: > 0 }
            ? $" accept=\"{GovUk.Esc(string.Join(",", field.AcceptedFileTypes))}\""
            : "";
        var capture = CaptureAttribute(field.CaptureMode) + PreviewAttribute(field.AcceptedFileTypes);
        var errorClass = error is null ? "" : " govuk-file-upload--error";
        var uploadedNotice = alreadyUploaded
            ? $"""<p class="govuk-body">Currently uploaded: {GovUk.Esc(field.Value?.ToString())}</p>"""
            : "";
        return $"""
            <div class="govuk-form-group{(error is null ? "" : " govuk-form-group--error")}">
              <label class="govuk-label" for="{id}">{GovUk.Esc(field.Label)}</label>
              {hint}
              {GovUkFields.ErrorMessage($"{id}-error", error)}
              {uploadedNotice}
              <input class="govuk-file-upload{errorClass}" id="{id}" name="{name}" type="file"{accept}{capture}{describedBy} {(alreadyUploaded ? "" : required)}>
            </div>
            """;
    }

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".heic", ".heif",
    };

    /// <summary>
    /// Whether any accepted file type is an image, so a chosen file can be previewed. Public so a host that
    /// renders its own file input (Wayfinder.Umbraco's progressive upload) loads the preview script on the
    /// same rule. A field with no accepted types could be anything, so it is never previewed.
    /// </summary>
    public static bool AcceptsImages(IReadOnlyList<string>? acceptedFileTypes) =>
        acceptedFileTypes is not null && acceptedFileTypes.Any(type => ImageTypes.Contains(type.Trim()));

    /// <summary>
    /// The attribute <c>wayfinder-file-preview.js</c> looks for, so an image input shows what was chosen: the
    /// control the browser draws for a chosen file is not a reliable preview (a phone's webview can draw it black).
    /// </summary>
    public static string PreviewAttribute(IReadOnlyList<string>? acceptedFileTypes) =>
        AcceptsImages(acceptedFileTypes) ? " data-wayfinder-file-preview" : "";

    /// <summary>
    /// The <c>capture</c> attribute for a capture mode, or nothing. Public so a host that renders its
    /// own file input (Wayfinder.Umbraco's progressive upload) applies the same mapping.
    /// </summary>
    public static string CaptureAttribute(string? captureMode) =>
        string.Equals(captureMode, "camera", StringComparison.OrdinalIgnoreCase) ? " capture=\"environment\"" : "";
}
