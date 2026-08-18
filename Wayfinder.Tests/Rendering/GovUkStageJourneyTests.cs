using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// <see cref="GovUkStageJourney"/> — first direct unit coverage for these four functions (moved
/// here from Wayfinder.ReferenceApp/Program.cs, where they were only ever reachable indirectly via
/// a full Playwright run). Two of these bodies document real, previously-fixed data-loss bugs —
/// this file exists to pin those regressions down with a fast, direct test, not just a slow
/// end-to-end one.
/// </summary>
public class GovUkStageJourneyTests
{
    private static ServiceRequestResponseEnvelope Envelope(StepContent? render, params ServiceRequestProblem[] problems) => new()
    {
        InstanceId = "instance-1",
        ResponseState = render is null ? "error" : "render",
        StateVersion = 1,
        CorrelationId = "correlation-1",
        ServerTimeUtc = DateTimeOffset.UtcNow,
        Render = render,
        Problems = problems
    };

    private static StepContent Step(string stepType, string stateDisplayName, params ComponentRenderPayload[] components) => new()
    {
        StepType = stepType,
        StateDisplayName = stateDisplayName,
        Components = components,
        AvailableActions = []
    };

    [Fact]
    public void RenderJourneyBody_OmitsTheHeading_WhenTheStageIsAConfirmationPanel()
    {
        var renderer = new GovUkComponentRenderer();
        var envelope = Envelope(Step("confirmation", "Done", new ComponentRenderPayload { Type = "panel", Heading = "Done" }));

        var body = renderer.RenderJourneyBody(envelope, "/advance");

        body.Should().NotContain("govuk-heading-xl", "a panel component already renders its own <h1> — a second heading would duplicate it");
    }

    [Fact]
    public void RenderJourneyBody_IncludesTheHeading_ForAnOrdinaryQuestionStage()
    {
        var renderer = new GovUkComponentRenderer();
        var envelope = Envelope(Step("question", "Your details",
            new ComponentRenderPayload { Type = "fieldset", Fields = [] }));

        var body = renderer.RenderJourneyBody(envelope, "/advance");

        body.Should().Contain("""<h1 class="govuk-heading-xl">Your details</h1>""");
    }

    [Fact]
    public void RenderJourneyBody_RendersTheFirstProblemMessage_WhenThereIsNoRenderPayload()
    {
        var renderer = new GovUkComponentRenderer();
        var envelope = Envelope(null, new ServiceRequestProblem { FieldKey = "", Message = "Access denied.", Code = "ACCESS_DENIED" });

        var body = renderer.RenderJourneyBody(envelope, "/advance");

        body.Should().Contain("Access denied.");
    }

    [Fact]
    public void WithFileDownloadUrls_OnlyStampsAUrl_OnAFileUploadFieldWithARealValue()
    {
        var envelope = Envelope(Step("question", "Upload",
            new ComponentRenderPayload
            {
                Type = "fieldset",
                Fields =
                [
                    new FieldRenderPayload { FieldKey = "riskAssessment", Label = "Risk assessment", FieldType = "file-upload", Required = false, Value = "ref:abc123" },
                    new FieldRenderPayload { FieldKey = "emptyUpload", Label = "Optional evidence", FieldType = "file-upload", Required = false, Value = null },
                    new FieldRenderPayload { FieldKey = "notes", Label = "Notes", FieldType = "text", Required = false, Value = "hello" }
                ]
            }));

        var result = envelope.WithFileDownloadUrls("/caseworker/queue/juggling-licence/instance-1/files");

        var fields = result.Render!.Components[0].Fields;
        fields.Single(f => f.FieldKey == "riskAssessment").FileUrl.Should().Be("/caseworker/queue/juggling-licence/instance-1/files/riskAssessment");
        fields.Single(f => f.FieldKey == "emptyUpload").FileUrl.Should().BeNull("an empty file-upload field keeps rendering 'Not provided', not a link to a 404");
        fields.Single(f => f.FieldKey == "notes").FileUrl.Should().BeNull("only file-upload fields are eligible at all");
    }

    [Fact]
    public void WithBulkDatasetApiUrls_OnlyStampsAUrl_OnAComponentWithARealDatasetId()
    {
        var envelope = Envelope(Step("question", "Review",
            new ComponentRenderPayload { Type = "bulk-data-review", DatasetId = "dataset-1" },
            new ComponentRenderPayload { Type = "bulk-data-review", DatasetId = null }));

        var result = envelope.WithBulkDatasetApiUrls("/caseworker/queue/njf-contributions/instance-1/bulk-datasets");

        result.Render!.Components[0].BulkDatasetApiUrl.Should().Be("/caseworker/queue/njf-contributions/instance-1/bulk-datasets/dataset-1");
        result.Render.Components[1].BulkDatasetApiUrl.Should().BeNull("nothing ingested yet keeps rendering its own placeholder, not a link to a 404");
    }

    private static IFormCollection Form(params (string Key, string Value)[] entries) =>
        new FormCollection(entries.ToDictionary(e => e.Key, e => new StringValues(e.Value)));

    [Fact]
    public void CoerceFieldValues_NeverCoercesASummaryListsFields_EvenWhenTheyShareAKeyWithAnEditableField()
    {
        // The exact regression this function's own doc comment documents: a summary-list is
        // read-only, never posted back — coercing it as though it had been silently turns a
        // displayed "yes" into "false" the moment the containing stage is submitted.
        var step = Step("check-answers", "Check your answers",
            new ComponentRenderPayload
            {
                Type = "summary-list",
                Fields = [new FieldRenderPayload { FieldKey = "hasDangerousProps", Label = "Dangerous props", FieldType = "boolean", Required = false, Value = true }]
            });
        var form = Form(); // nothing posted — a summary-list's own fields are never on the form at all

        var result = GovUkStageJourney.CoerceFieldValues(form, step);

        result.Should().NotContainKey("hasDangerousProps");
    }

    [Fact]
    public void CoerceFieldValues_ABooleanField_IsFalseWhenAbsentFromTheForm_TrueWhenPresent()
    {
        var step = Step("question", "Declare",
            new ComponentRenderPayload { Type = "fieldset", Fields = [new FieldRenderPayload { FieldKey = "confirm", Label = "Confirm", FieldType = "boolean", Required = false }] });

        GovUkStageJourney.CoerceFieldValues(Form(), step)["confirm"].Should().Be(false, "an unchecked checkbox posts nothing at all — absence IS false");
        GovUkStageJourney.CoerceFieldValues(Form(("field:confirm", "true")), step)["confirm"].Should().Be(true);
    }

    [Fact]
    public void CoerceFieldValues_NeverCoercesAFileUploadField_ExclusivelyStageFileUploadsConcern()
    {
        var step = Step("question", "Upload",
            new ComponentRenderPayload { Type = "fieldset", Fields = [new FieldRenderPayload { FieldKey = "evidence", Label = "Evidence", FieldType = "file-upload", Required = false }] });
        // A file input with nothing newly selected still posts a real (empty) multipart section —
        // simulated here by a present-but-blank form value, which the generic branch would
        // otherwise stamp as "" and silently wipe an already-uploaded file's reference.
        var form = Form(("field:evidence", ""));

        var result = GovUkStageJourney.CoerceFieldValues(form, step);

        result.Should().NotContainKey("evidence", "file-upload is exclusively StageFileUploads.ApplyFileUploadsAsync's concern");
    }

    [Fact]
    public void CoerceFieldValues_ADateField_CombinesTheThreePostedBoxes()
    {
        var step = Step("question", "Your details",
            new ComponentRenderPayload { Type = "fieldset", Fields = [new FieldRenderPayload { FieldKey = "dob", Label = "Date of birth", FieldType = "date", Required = false }] });
        var form = Form(("field:dob-day", "5"), ("field:dob-month", "9"), ("field:dob-year", "1990"));

        var result = GovUkStageJourney.CoerceFieldValues(form, step);

        result["dob"].Should().Be("1990-09-05");
    }

    [Fact]
    public void CoerceFieldValues_ANumberField_ParsesToADecimal()
    {
        var step = Step("question", "About the event",
            new ComponentRenderPayload { Type = "fieldset", Fields = [new FieldRenderPayload { FieldKey = "jugglers", Label = "Number of jugglers", FieldType = "number", Required = false }] });

        var result = GovUkStageJourney.CoerceFieldValues(Form(("field:jugglers", "12")), step);

        result["jugglers"].Should().Be(12m);
    }

    [Fact]
    public void CoerceFieldValues_APlainTextField_NotPostedAtAll_IsLeftAbsent()
    {
        var step = Step("question", "Your details",
            new ComponentRenderPayload { Type = "fieldset", Fields = [new FieldRenderPayload { FieldKey = "notes", Label = "Notes", FieldType = "text", Required = false }] });

        var result = GovUkStageJourney.CoerceFieldValues(Form(), step);

        result.Should().NotContainKey("notes");
    }

    [Fact]
    public void CoerceFieldValues_NullRender_ReturnsAnEmptyDictionary()
    {
        GovUkStageJourney.CoerceFieldValues(Form(("field:anything", "x")), null).Should().BeEmpty();
    }
}
