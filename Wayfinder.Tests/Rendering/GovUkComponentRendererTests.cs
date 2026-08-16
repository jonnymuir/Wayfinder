using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>
/// Proves the shared package's built-in catalog actually renders every type it claims to cover
/// — real markup for types beyond the 5 the reference app's old hand-rolled renderer supported —
/// not just that it compiles.
/// </summary>
public class GovUkComponentRendererTests
{
    private static readonly GovUkComponentRenderer Renderer = new();
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    private static string RenderComponentOnly(ComponentRenderPayload component)
    {
        var step = new StepContent
        {
            StepType = "question",
            StateDisplayName = "Test",
            Components = [component],
            AvailableActions = [],
        };
        return Renderer.RenderForm(step, [], "/test", 0);
    }

    [Fact]
    public void RenderField_Radio_ProducesRealGdsRadiosMarkup()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "colour",
            Label = "Colour",
            FieldType = "radio",
            Required = true,
            Options = ["Red", "Blue"],
        }, NoErrors);

        Assert.Contains("govuk-radios", html);
        Assert.Contains("type=\"radio\"", html);
        Assert.Contains("Red", html);
        Assert.Contains("Blue", html);
    }

    [Fact]
    public void RenderField_Select_ProducesRealGdsSelectMarkup()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "size",
            Label = "Size",
            FieldType = "select",
            Required = true,
            Options = ["Small", "Large"],
        }, NoErrors);

        Assert.Contains("govuk-select", html);
        Assert.Contains("<option value=\"Small\">", html);
    }

    [Fact]
    public void RenderField_CheckboxList_ProducesRealGdsCheckboxesMarkup()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "hazards",
            Label = "Hazards",
            FieldType = "checkboxlist",
            Required = false,
            Options = ["Fire", "Knives"],
        }, NoErrors);

        Assert.Contains("govuk-checkboxes", html);
        Assert.Contains("name=\"field:hazards[]\"", html);
    }

    [Fact]
    public void RenderField_Slider_ProducesRealInputRange()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "risk",
            Label = "Risk appetite",
            FieldType = "slider",
            Required = true,
            Min = 0,
            Max = 10,
        }, NoErrors);

        Assert.Contains("type=\"range\"", html);
        Assert.Contains("min=\"0\"", html);
        Assert.Contains("max=\"10\"", html);
        Assert.Contains("data-wayfinder-slider", html);
        Assert.Contains("wayfinder-slider__input", html);
    }

    // Regression coverage: id and name used to share the same "field:{fieldKey}"-prefixed
    // string here too, breaking any plain CSS ID selector targeting the rendered input.
    [Fact]
    public void RenderField_Slider_IdStaysBareFieldKey_NameCarriesFieldPrefix()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "risk",
            Label = "Risk appetite",
            FieldType = "slider",
            Required = true,
            Min = 0,
            Max = 10,
        }, NoErrors);

        Assert.Contains("id=\"risk\"", html);
        Assert.Contains("for=\"risk\"", html);
        Assert.Contains("name=\"field:risk\"", html);
        Assert.DoesNotContain("id=\"field:risk\"", html);
    }

    [Fact]
    public void RenderField_FileUpload_ProducesRealGdsFileUploadMarkup()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "evidence",
            Label = "Evidence",
            FieldType = "file-upload",
            Required = true,
        }, NoErrors);

        Assert.Contains("govuk-file-upload", html);
        Assert.Contains("type=\"file\"", html);
    }

    [Fact]
    public void RenderField_GuidanceChecklist_ShowsProgressAndItems()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "guidance",
            Label = "Read the guidance",
            FieldType = "guidance-checklist",
            Required = true,
            GuidanceItems =
            [
                new() { Key = "safety", Label = "Safety guidance", Href = "https://example.test/safety" },
            ],
        }, NoErrors);

        Assert.Contains("0 of 1 guidance articles completed", html);
        Assert.Contains("Safety guidance", html);
    }

    [Fact]
    public void RenderField_ShowsErrorMessageWhenFieldHasAProblem()
    {
        var errors = new Dictionary<string, string> { ["name"] = "Full name is required." };
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "name",
            Label = "Full name",
            FieldType = "text",
            Required = true,
        }, errors);

        Assert.Contains("govuk-error-message", html);
        Assert.Contains("Full name is required.", html);
    }

    [Fact]
    public void RenderComponent_Accordion_RendersSectionsAndNestedFields()
    {
        var html = RenderComponentOnly(new ComponentRenderPayload
        {
            Type = "accordion",
            AccordionSections =
            [
                new()
                {
                    Heading = "Section one",
                    Fields = [new FieldRenderPayload { FieldKey = "note", Label = "Note", FieldType = "text", Required = false }],
                },
            ],
        });

        Assert.Contains("govuk-accordion", html);
        Assert.Contains("Section one", html);
        Assert.Contains("field:note", html);
    }

    [Fact]
    public void RenderComponent_StatGroup_RendersEachStat()
    {
        var html = RenderComponentOnly(new ComponentRenderPayload
        {
            Type = "stat-group",
            Title = "Key figures",
            Stats =
            [
                new() { Label = "Annual pension", FieldKey = "pension", Value = "16400", Qualifier = "a year, for life" },
            ],
        });

        Assert.Contains("Annual pension", html);
        Assert.Contains("16400", html);
        Assert.Contains("wayfinder-stat-group", html);
        Assert.Contains("wayfinder-stat-card", html);
    }

    [Fact]
    public void RenderComponent_TaskList_RendersTasksWithStatusTags()
    {
        var html = RenderComponentOnly(new ComponentRenderPayload
        {
            Type = "task-list",
            TaskSections =
            [
                new()
                {
                    Heading = "Before you start",
                    Tasks = [new() { Label = "Read the guidance", Href = "/guidance", Status = "completed" }],
                },
            ],
        });

        Assert.Contains("govuk-task-list", html);
        Assert.Contains("Read the guidance", html);
        Assert.Contains("completed", html);
    }

    [Fact]
    public void RenderComponent_Chart_RendersAccessibleTableFromChartJson()
    {
        var chartJson = """
            {
              "x": "age",
              "bands": [{ "key": "pension", "label": "Pension" }],
              "rows": [{ "age": 66, "pension": 12000 }]
            }
            """;
        var html = RenderComponentOnly(new ComponentRenderPayload
        {
            Type = "chart",
            Heading = "Projected income",
            ChartJson = chartJson,
        });

        Assert.Contains("data-wayfinder-chart-table", html);
        Assert.Contains("Pension", html);
        Assert.Contains("12000", html);
        Assert.Contains("wayfinder-chart", html);
        Assert.Contains("data-wayfinder-chart-plot", html);
    }

    [Fact]
    public void RenderComponent_Chart_EmptyChartJson_RendersNothing()
    {
        var html = Renderer.RenderComponent(new ComponentRenderPayload
        {
            Type = "chart",
            Heading = "Projected income",
            ChartJson = null,
        }, NoErrors);

        Assert.Equal("", html);
    }

    [Fact]
    public void RenderComponent_WrapsShowWhenComponentsWithHiddenAttribute()
    {
        var html = RenderComponentOnly(new ComponentRenderPayload
        {
            Type = "body",
            Content = "Only shown for fire acts",
            ShowWhen = "hasDangerousProps",
            Hidden = true,
        });

        Assert.Contains("data-wayfinder-show-when=\"hasDangerousProps\"", html);
        Assert.Contains("hidden", html);
    }

    [Fact]
    public void RegisterComponent_OverridesTheBuiltInRendererForThatTypeOnly()
    {
        var renderer = new GovUkComponentRenderer();
        renderer.RegisterComponent("panel", (component, _) => $"<custom-panel>{component.Heading}</custom-panel>");

        var step = new StepContent
        {
            StepType = "confirmation",
            StateDisplayName = "Done",
            Components = [new ComponentRenderPayload { Type = "panel", Heading = "Application received" }],
            AvailableActions = [],
        };
        var html = renderer.RenderForm(step, [], "/test", 0);

        Assert.Contains("<custom-panel>Application received</custom-panel>", html);
        Assert.DoesNotContain("govuk-panel", html);
    }

    // Regression coverage: id and name used to share the same "field:{fieldKey}"-prefixed
    // string. A colon in an id breaks any plain CSS ID selector targeting it (`#fieldKey`
    // matches nothing; `field:fieldKey` parses as a `field` id plus a `:fieldKey` pseudo-class)
    // — confirmed live via a Playwright journey hanging forever on a date field's day/month/year
    // sub-inputs, none of which a bare `#dateField-day` selector could ever find. id must stay
    // the bare field key; only name carries the field: prefix a host's own form-submission
    // parsing keys off.

    [Fact]
    public void RenderField_Text_IdStaysBareFieldKey_NameCarriesFieldPrefix()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "full-name",
            Label = "Full name",
            FieldType = "text",
            Required = true,
        }, NoErrors);

        Assert.Contains("id=\"full-name\"", html);
        Assert.Contains("for=\"full-name\"", html);
        Assert.Contains("name=\"field:full-name\"", html);
        Assert.DoesNotContain("id=\"field:full-name\"", html);
    }

    [Fact]
    public void RenderField_Date_SubInputIdsStayBare_NamesCarryFieldPrefix()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "date-of-birth",
            Label = "Date of birth",
            FieldType = "date",
            Required = true,
        }, NoErrors);

        Assert.Contains("id=\"date-of-birth-day\"", html);
        Assert.Contains("id=\"date-of-birth-month\"", html);
        Assert.Contains("id=\"date-of-birth-year\"", html);
        Assert.Contains("for=\"date-of-birth-day\"", html);
        Assert.Contains("name=\"field:date-of-birth-day\"", html);
        Assert.Contains("name=\"field:date-of-birth-month\"", html);
        Assert.Contains("name=\"field:date-of-birth-year\"", html);
        Assert.DoesNotContain("id=\"field:date-of-birth", html);
    }

    [Fact]
    public void RenderField_Boolean_IdStaysBareFieldKey_NameCarriesFieldPrefix()
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "age-confirmation",
            Label = "I confirm I am aged 16 or over",
            FieldType = "boolean",
            Required = true,
        }, NoErrors);

        Assert.Contains("id=\"age-confirmation\"", html);
        Assert.Contains("for=\"age-confirmation\"", html);
        Assert.Contains("name=\"field:age-confirmation\"", html);
        Assert.DoesNotContain("id=\"field:age-confirmation\"", html);
    }

    // Regression: a stored `true` reloads as the CLR/JsonElement spelling "True", not the
    // lowercase JSON literal — see FormatSummaryValue's own comment on this same ambiguity. A
    // previous case-sensitive `value == "true"` check here rendered the checkbox unchecked on
    // every GET re-render (revisiting a stage via a summary-list "Change" link, or a plain
    // reload), silently discarding the applicant's own answer.
    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void RenderField_Boolean_PreFillIsCaseInsensitive(string storedValue, bool expectChecked)
    {
        var html = GovUkFields.Render(new FieldRenderPayload
        {
            FieldKey = "has-dangerous-props",
            Label = "This act involves fire, knives, or other dangerous props",
            FieldType = "boolean",
            Required = false,
            Value = storedValue,
        }, NoErrors);

        if (expectChecked)
        {
            Assert.Contains("type=\"checkbox\" value=\"true\" checked", html);
        }
        else
        {
            Assert.DoesNotContain("checked", html);
        }
    }
}
