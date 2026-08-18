using Microsoft.AspNetCore.Http;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// The generic, blueprint-agnostic glue every host route rendering a stage/form ends up needing —
/// extracted here (rather than left hand-copied per host, or per route within one host) because
/// none of it references any host-specific concept: it's built entirely from
/// <see cref="ServiceRequestResponseEnvelope"/>/<see cref="StepContent"/>'s own shape and this
/// package's own <see cref="GovUkComponentRenderer"/>. <c>IFormCollection</c> is base ASP.NET Core
/// HTTP, not an MVC-specific type — this package's own "no Razor/MVC dependency" promise still
/// holds; see the package's own <c>.csproj</c> comment on why <c>Sdk.Web</c> already gives it this
/// framework reference for free.
/// </summary>
public static class GovUkStageJourney
{
    /// <summary>
    /// Renders a stage's own heading (skipped when the stage is a confirmation panel — it already
    /// renders its own <c>&lt;h1&gt;</c>, and a second heading here would be a duplicate the real
    /// GOV.UK panel component isn't designed to sit under) plus its form body.
    /// </summary>
    public static string RenderJourneyBody(this GovUkComponentRenderer renderer, ServiceRequestResponseEnvelope envelope, string formAction)
    {
        var esc = GovUk.Esc;
        if (envelope.Render is null)
        {
            var message = envelope.Problems.FirstOrDefault()?.Message ?? "Nothing to show.";
            return $"""<p class="govuk-body">{esc(message)}</p>""";
        }

        var heading = GovUkComponentRenderer.HasPanel(envelope.Render)
            ? ""
            : $"""<h1 class="govuk-heading-xl">{esc(envelope.Render.StateDisplayName)}</h1>""";

        return $"{heading}{renderer.RenderForm(envelope.Render, envelope.Problems, formAction, envelope.StateVersion)}";
    }

    /// <summary>
    /// A caseworker reviewing an application needs to actually open what was uploaded, not just
    /// read its filename. The engine deliberately can't do this itself — it only ever holds an
    /// opaque <see cref="ServiceRequestFileReference"/> and knows nothing about a host's URL space
    /// (see <c>IServiceRequestFileStorage</c>: the host owns storage *and* routing) — so the host
    /// fills in <see cref="FieldRenderPayload.FileUrl"/> on the way to the renderer, which turns
    /// the summary row's filename into a real link. That's why viewing an uploaded file needs no
    /// new component type: it's a host rendering concern hung off the existing
    /// <c>file-upload</c> field.
    ///
    /// Generic over every file-upload field on the stage, so it needs no per-blueprint wiring —
    /// any new file-upload field anywhere starts working here too. Only a field with a real value
    /// gets a URL; an empty one keeps rendering "Not provided" rather than linking to a 404.
    /// </summary>
    public static ServiceRequestResponseEnvelope WithFileDownloadUrls(
        this ServiceRequestResponseEnvelope envelope,
        string downloadUrlPrefix)
    {
        if (envelope.Render is null)
        {
            return envelope;
        }

        var components = envelope.Render.Components
            .Select(component => component.Fields.Any(field => field.FieldType == "file-upload")
                ? component with
                {
                    Fields = component.Fields
                        .Select(field => field.FieldType == "file-upload" && !string.IsNullOrEmpty(field.Value?.ToString())
                            ? field with { FileUrl = $"{downloadUrlPrefix}/{Uri.EscapeDataString(field.FieldKey)}" }
                            : field)
                        .ToArray()
                }
                : component)
            .ToArray();

        return envelope with { Render = envelope.Render with { Components = components } };
    }

    /// <summary>
    /// Same reasoning as <see cref="WithFileDownloadUrls"/>, for the bulk-data-review component's
    /// own REST endpoints (see docs/guides/bulk-data-review.md): the engine resolves a
    /// "bulk-data-review" component's <c>DatasetId</c> from field values, but has no opinion on a
    /// host's own URL scheme, so it's the host's job to fill in
    /// <see cref="ComponentRenderPayload.BulkDatasetApiUrl"/> before rendering. Only a component
    /// with a real dataset id gets a URL — one with none yet (nothing ingested) keeps rendering
    /// its own "Nothing to review yet" placeholder rather than linking to a 404.
    /// </summary>
    public static ServiceRequestResponseEnvelope WithBulkDatasetApiUrls(
        this ServiceRequestResponseEnvelope envelope,
        string apiUrlPrefix)
    {
        if (envelope.Render is null)
        {
            return envelope;
        }

        var components = envelope.Render.Components
            .Select(component => component.Type == "bulk-data-review" && !string.IsNullOrEmpty(component.DatasetId)
                ? component with { BulkDatasetApiUrl = $"{apiUrlPrefix}/{Uri.EscapeDataString(component.DatasetId)}" }
                : component)
            .ToArray();

        return envelope with { Render = envelope.Render with { Components = components } };
    }

    /// <summary>
    /// Reads posted <c>field:{fieldKey}</c> values back into the CLR shapes the engine expects,
    /// using the field-type map from the stage that produced the form (a checkbox posts nothing at
    /// all when unchecked, so boolean fields need explicit false; number/decimal fields parse to a
    /// real number rather than staying a string).
    /// </summary>
    public static Dictionary<string, object?> CoerceFieldValues(IFormCollection form, StepContent? render)
    {
        var fieldValues = new Dictionary<string, object?>();
        if (render is null)
        {
            return fieldValues;
        }

        // Only components that actually render editable controls. A summary-list is always a
        // read-only display of values captured earlier (GovUkComponents.RenderSummaryList is
        // deliberately not routed through the overridable field renderer for exactly this reason),
        // so its rows are never posted back — and must never be *coerced* as though they had been.
        //
        // This mattered, silently and destructively: the boolean branch below writes
        // `form.ContainsKey(...)` unconditionally, because an unchecked checkbox genuinely posts
        // nothing and "absent" is the only way to detect false. Applied to a read-only summary row
        // that was never on the form, that turns every displayed-but-not-editable boolean into
        // false the moment the stage is submitted. On juggling-licence, submitting "check your
        // answers" (whose summary shows hasDangerousProps) wiped the applicant's own "yes" — so
        // the caseworker reviewing a fire act read "Fire, knives or other dangerous props: No".
        // Found by watching a recorded end-to-end take contradict its own narration.
        var fieldsByKey = render.Components
            .Where(component => component.Type != "summary-list")
            .SelectMany(component => component.Fields)
            .ToDictionary(field => field.FieldKey, field => field.FieldType, StringComparer.Ordinal);

        foreach (var (fieldKey, fieldType) in fieldsByKey)
        {
            var formKey = $"field:{fieldKey}";

            if (fieldType == "boolean")
            {
                fieldValues[fieldKey] = form.ContainsKey(formKey);
                continue;
            }

            // file-upload is exclusively StageFileUploads.ApplyFileUploadsAsync's concern, never
            // this generic text branch's. An <input type="file"> with nothing newly selected still
            // posts a real multipart section for its field name — empty filename, zero bytes — and
            // that section's own Content-Disposition also satisfies form.TryGetValue, so without
            // this guard the generic branch below would set fieldValues[fieldKey] = "" explicitly.
            // ApplyFileUploadsAsync correctly leaves an unchanged file field's key absent when
            // nothing new was posted (letting Merge preserve whatever's already stored) — but that
            // protection is worthless if this loop already stamped an explicit "" over the same
            // key first. Reproduced live: revisiting an earlier stage via a "Change" link and
            // continuing back through a file-upload stage without reselecting a file silently
            // wiped the already-uploaded file's reference the moment the stage was resubmitted.
            if (fieldType == "file-upload")
            {
                continue;
            }

            // The real GOV.UK date-input component posts three separate day/month/year fields
            // rather than one native date value — see this package's own GovUkFields date field.
            if (fieldType == "date")
            {
                var combined = GovUk.CombineIsoDate(
                    form[$"{formKey}-day"], form[$"{formKey}-month"], form[$"{formKey}-year"]);
                if (combined is not null)
                {
                    fieldValues[fieldKey] = combined;
                }
                continue;
            }

            if (!form.TryGetValue(formKey, out var raw))
            {
                continue;
            }

            fieldValues[fieldKey] = fieldType switch
            {
                "number" or "decimal" => decimal.TryParse(raw, out var number) ? number : raw.ToString(),
                _ => raw.ToString()
            };
        }

        return fieldValues;
    }
}
