using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>The field types whose built-in rendering lives in a file of its own.</summary>
internal static class GovUkFieldRenderers
{
    private static readonly Dictionary<string, Func<FieldRenderPayload, IReadOnlyDictionary<string, string>, string>> ByFieldType = new()
    {
        ["file-upload"] = GovUkFileUploadField.Render,
        ["guidance-checklist"] = GovUkGuidanceChecklistField.Render,
        ["location-picker"] = GovUkLocationPickerField.Render,
        ["slider"] = GovUkSliderField.Render,
    };

    internal static bool TryGet(string fieldType, out Func<FieldRenderPayload, IReadOnlyDictionary<string, string>, string> render) =>
        ByFieldType.TryGetValue(fieldType, out render!);
}
