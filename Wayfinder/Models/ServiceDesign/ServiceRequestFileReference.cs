using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Reference to a file uploaded against a <c>file-upload</c> field, stored as the field's
/// value in <c>ServiceRequest.FieldValues</c>. Round-trips through JSON persistence —
/// never carries the file bytes themselves, only enough to locate and describe them via a
/// host-registered file storage service.
/// </summary>
public sealed record ServiceRequestFileReference
{
    /// <summary>Opaque key identifying the stored file — never the original filename.</summary>
    public string StorageKey { get; init; } = "";

    /// <summary>The filename as uploaded by the visitor, for display and download purposes only.</summary>
    public string OriginalFileName { get; init; } = "";

    /// <summary>The uploaded file's content type.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>The uploaded file's size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Parses a <c>file-upload</c> field's raw stored value back into a reference. Handles a
    /// same-request value (still its original CLR type), a reloaded one (a boxed
    /// <see cref="JsonElement"/>, since <c>FieldValues</c> has no custom converter), and a
    /// same-request value that arrived via a support-system capability's own file-typed
    /// <c>Output</c> — <c>ProcessManagerEngine.ResolveSupportSystemOutcome</c>'s
    /// <c>ToFieldValues</c> merges an object-shaped result-payload value in as a
    /// <see cref="JsonObject"/> (a <c>DeepClone()</c> of the payload node), a different CLR type
    /// than the <see cref="JsonElement"/> a reload produces, so both need their own case. Returns
    /// <see langword="null"/> for anything else, including no value at all.
    /// </summary>
    public static ServiceRequestFileReference? FromFieldValue(object? raw) => raw switch
    {
        ServiceRequestFileReference reference => reference,
        JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object =>
            jsonElement.Deserialize<ServiceRequestFileReference>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
        JsonObject jsonObject =>
            jsonObject.Deserialize<ServiceRequestFileReference>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
        _ => null
    };
}
