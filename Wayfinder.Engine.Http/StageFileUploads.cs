using Microsoft.AspNetCore.Http;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Http;

/// <summary>
/// The one piece of stage-form request-processing glue that genuinely needs
/// <see cref="IServiceRequestFileStorage"/> (hence its own package, distinct from
/// <c>Wayfinder.Rendering.GovUk</c>'s <c>GovUkStageJourney</c>, which deliberately has no
/// <c>Wayfinder.Engine</c> dependency at all).
/// </summary>
public static class StageFileUploads
{
    /// <summary>
    /// Handles every <c>file-upload</c> field on the current stage: validates a posted file against
    /// its own declared <c>MaxSizeBytes</c>/<c>AcceptedFileTypes</c> — server-side, since the engine
    /// itself never sees bytes and can't be the enforcement point (see
    /// <see cref="IServiceRequestFileStorage"/>) — then saves it and writes the resulting reference
    /// into <paramref name="fieldValues"/>. A field with no file posted this time round is left
    /// untouched entirely, so the engine's own merge preserves whatever reference (if any) the
    /// instance already has stored, the same as any other unchanged field. Returns one
    /// <see cref="ServiceRequestProblem"/> per rejected file; empty means every posted file was
    /// accepted (or none were posted at all).
    /// </summary>
    public static async Task<List<ServiceRequestProblem>> ApplyFileUploadsAsync(
        IFormCollection form, StepContent? render, string instanceId, IServiceRequestFileStorage fileStorage, Dictionary<string, object?> fieldValues)
    {
        const long defaultMaxSizeBytes = 10 * 1024 * 1024;
        var problems = new List<ServiceRequestProblem>();
        if (render is null)
        {
            return problems;
        }

        var fileUploadFields = render.Components
            .SelectMany(component => component.Fields)
            .Where(field => field.FieldType == "file-upload");

        foreach (var field in fileUploadFields)
        {
            var formKey = $"field:{field.FieldKey}";
            var file = form.Files.GetFile(formKey);
            if (file is null || file.Length == 0)
            {
                continue; // Nothing new posted — leave the instance's existing reference (if any) untouched.
            }

            var maxSizeBytes = field.MaxSizeBytes ?? defaultMaxSizeBytes;
            if (file.Length > maxSizeBytes)
            {
                problems.Add(new ServiceRequestProblem
                {
                    FieldKey = field.FieldKey,
                    Message = $"{field.Label} must be smaller than {maxSizeBytes / (1024 * 1024)}MB.",
                    Code = "VALIDATION_ERROR"
                });
                continue;
            }

            var extension = Path.GetExtension(file.FileName);
            if (field.AcceptedFileTypes is { Count: > 0 } accepted
                && !accepted.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(new ServiceRequestProblem
                {
                    FieldKey = field.FieldKey,
                    Message = $"{field.Label} must be one of: {string.Join(", ", accepted)}.",
                    Code = "VALIDATION_ERROR"
                });
                continue;
            }

            await using var stream = file.OpenReadStream();
            var storageKey = await fileStorage.SaveAsync(instanceId, field.FieldKey, stream, file.FileName);
            // The engine's own GetDisplayValue (ProcessManagerEngine) only recognises a file-upload
            // field's persisted value as a ServiceRequestFileReference (or its JsonElement round
            // trip) — a bare storage-key string displays as nothing at all, which also leaves the
            // rendered <input> incorrectly marked required after a validation bounce-back, since a
            // browser can't pre-populate a file input from a prior selection.
            fieldValues[field.FieldKey] = new ServiceRequestFileReference
            {
                StorageKey = storageKey,
                OriginalFileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
            };
        }

        return problems;
    }
}
