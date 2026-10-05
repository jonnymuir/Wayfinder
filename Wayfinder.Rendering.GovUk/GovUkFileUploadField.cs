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
        var capture = CaptureAttribute(field.CaptureMode);
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

    /// <summary>
    /// The <c>capture</c> attribute for a capture mode, or nothing. Public so a host that renders its
    /// own file input (Wayfinder.Umbraco's progressive upload) applies the same mapping.
    /// </summary>
    public static string CaptureAttribute(string? captureMode) =>
        string.Equals(captureMode, "camera", StringComparison.OrdinalIgnoreCase) ? " capture=\"environment\"" : "";
}
