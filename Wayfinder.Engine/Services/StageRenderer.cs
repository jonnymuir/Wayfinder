using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Engine.Services;

/// <summary>Turns a stage's authored components and an instance's values into the render payload a client draws.</summary>
internal sealed partial class StageRenderer(IServiceContentSanitizer sanitizer, StageCalculations calculations, ILogger logger)
{
    public ComponentRenderPayload[] BuildComponents(
        IReadOnlyList<Component> componentDefinitions,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        // Stat-groups and summary-lists resolve display values from the calculation
        // overlay when one exists; plain input values come from the instance as before.
        var displayValues = calc is null
            ? savedValues
            : new Dictionary<string, object?>(calc.DisplayValues, StringComparer.Ordinal);

        var result = new List<ComponentRenderPayload>();

        foreach (var component in componentDefinitions)
        {
            var payloadsBefore = result.Count;
            switch (component)
            {
                case FieldsetComponent fieldset:
                {
                    var fields = BuildFields(fieldset.Children, displayValues, calc);
                    if (fields.Length == 0)
                    {
                        FieldsetHasNoFields(logger);
                        continue;
                    }

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "fieldset",
                        Legend = fieldset.Legend,
                        LegendSize = fieldset.LegendSize,
                        Fields = fields
                    });
                    break;
                }

                case SummaryListComponent summary:
                {
                    // A summary-list echoes values already collected (and validated) on the
                    // stages its "change" links point back to — never a fresh submission on
                    // whatever stage/transition happens to render it (e.g. a check-answers
                    // page's own "submit"). ReadOnly = true keeps FieldValueValidator from
                    // demanding these be resubmitted alongside that stage's own real fields.
                    var fields = BuildFields(summary.Children, displayValues, calc)
                        .Select(f => f with { ReadOnly = true })
                        .ToArray();
                    if (fields.Length == 0)
                    {
                        SummaryListHasNoFields(logger);
                        continue;
                    }

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "summary-list",
                        Title = summary.Title,
                        SourceStateKey = summary.ChangeStateKey,
                        Fields = fields
                    });
                    break;
                }

                case AccordionComponent accordion:
                {
                    var sections = accordion.Sections
                        .Select(section => new AccordionSectionPayload
                        {
                            Heading = section.Heading,
                            Summary = section.Summary,
                            Fields = BuildFields(section.Children, displayValues, calc)
                        })
                        .ToArray();

                    result.Add(new ComponentRenderPayload
                    {
                        Type = "accordion",
                        AccordionSections = sections
                    });
                    break;
                }

                case WaitingComponent waiting:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "waiting",
                        Content = sanitizer.Sanitize(waiting.Content),
                        ExpectedWaitSeconds = waiting.ExpectedWaitSeconds,
                        PollIntervalMs = waiting.PollIntervalMs,
                        AllowDefer = waiting.AllowDefer,
                        DeferMessage = waiting.DeferMessage
                    });
                    break;

                case PanelComponent panel:
                    result.Add(new ComponentRenderPayload { Type = "panel", Heading = panel.Heading });
                    break;

                case BodyComponent body:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "body",
                        Content = sanitizer.Sanitize(body.Content)
                    });
                    break;

                case HeadingComponent heading:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "heading",
                        Content = heading.Content,
                        Level = heading.Level
                    });
                    break;

                case InsetTextComponent inset:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "inset-text",
                        Content = sanitizer.Sanitize(inset.Content)
                    });
                    break;

                case WarningTextComponent warning:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "warning-text",
                        Content = sanitizer.Sanitize(warning.Content)
                    });
                    break;

                case DetailsComponent details:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "details",
                        Heading = details.Heading,
                        Content = sanitizer.Sanitize(details.Content)
                    });
                    break;

                case NotificationBannerComponent banner:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "notification-banner",
                        Heading = banner.Heading,
                        Content = sanitizer.Sanitize(banner.Content),
                        BannerType = banner.BannerType
                    });
                    break;

                case TaskListComponent taskList:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "task-list",
                        TaskSections = taskList.Sections?.Select(section => new TaskSectionPayload
                        {
                            Heading = section.Heading,
                            Tasks = section.Tasks.Select(task => new TaskItemPayload
                            {
                                Label = task.Label,
                                Href = task.Href ?? task.StageKey,
                                Status = "not-started"
                            }).ToArray()
                        }).ToArray()
                    });
                    break;

                case StatGroupComponent statGroup:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "stat-group",
                        Title = statGroup.Title,
                        Stats = statGroup.Items.Select(item => new StatItem
                        {
                            Label = item.Label,
                            FieldKey = item.FieldKey,
                            Value = displayValues.TryGetValue(item.FieldKey, out var statValue)
                                ? statValue?.ToString()
                                : null,
                            Qualifier = item.Qualifier,
                            Emphasis = item.Emphasis
                        }).ToArray()
                    });
                    break;

                case ChartComponent chart:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "chart",
                        Heading = chart.Title,
                        ChartJson = BuildChartJson(chart, calc)
                    });
                    break;

                case BulkDataReviewComponent bulkReview:
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "bulk-data-review",
                        Title = bulkReview.Title,
                        DatasetId = displayValues.TryGetValue(bulkReview.DatasetIdField, out var datasetIdValue)
                            ? datasetIdValue?.ToString()
                            : null,
                        PageSize = bulkReview.PageSize,
                        SyncedLabel = bulkReview.SyncedLabel,
                        PendingLabel = bulkReview.PendingLabel,
                        SinceLabel = bulkReview.SinceLabel,
                    });
                    break;

                case InputComponent input:
                {
                    var fields = BuildFields(new[] { (Component)input }, displayValues, calc);
                    result.Add(new ComponentRenderPayload
                    {
                        Type = "fieldset",
                        Fields = fields
                    });
                    break;
                }
            }

            if (component.ShowWhen is { Length: > 0 } showWhen)
            {
                var visible = calculations.EvaluateShowWhen(showWhen, calc?.Scope, calc?.Set);
                for (var i = payloadsBefore; i < result.Count; i++)
                {
                    result[i] = result[i] with { ShowWhen = showWhen, Hidden = !visible };
                }
            }
        }

        return result.ToArray();
    }

    private static string BuildChartJson(ChartComponent chart, CalculationRenderContext? calc)
    {
        var bands = new JsonArray();
        foreach (var band in chart.Bands)
        {
            bands.Add(new JsonObject
            {
                ["key"] = band.Key,
                ["label"] = band.Label,
                ["color"] = band.Color
            });
        }

        var rows = new JsonArray();
        if (calc is not null && calc.Result.Series.TryGetValue(chart.Series, out var seriesRows))
        {
            foreach (var seriesRow in seriesRows)
            {
                var row = new JsonObject();
                foreach (var (column, value) in seriesRow)
                {
                    row[column] = StageCalculations.ScopeValueToJson(value);
                }

                rows.Add(row);
            }
        }

        return new JsonObject
        {
            ["kind"] = chart.Kind,
            ["x"] = chart.X,
            ["xLabelEvery"] = chart.XLabelEvery,
            ["series"] = chart.Series,
            ["bands"] = bands,
            ["rows"] = rows
        }.ToJsonString();
    }

    private static FieldRenderPayload[] BuildFields(
        IEnumerable<Component> children,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        var fields = new List<FieldRenderPayload>();

        foreach (var child in children)
        {
            switch (child)
            {
                case InputComponent input:
                    fields.Add(BuildInputPayload(input, savedValues, calc));

                    var conditional = (child as RadiosComponent)?.ConditionalChildren
                                      ?? (child as CheckboxesComponent)?.ConditionalChildren;
                    if (conditional != null)
                    {
                        foreach (var (optionValue, subComponents) in conditional)
                        {
                            foreach (var sub in subComponents.GetAllInputs())
                            {
                                fields.Add(BuildInputPayload(sub, savedValues, calc) with
                                {
                                    ConditionalOn = input.FieldKey,
                                    VisibleWhen = optionValue
                                });
                            }
                        }
                    }

                    break;

                case FieldsetComponent nestedFieldset:
                    fields.AddRange(BuildFields(nestedFieldset.Children, savedValues, calc));
                    break;
            }
        }

        return fields.ToArray();
    }

    private static FieldRenderPayload BuildInputPayload(
        InputComponent input,
        Dictionary<string, object?> savedValues,
        CalculationRenderContext? calc = null)
    {
        var fieldType = InputFieldType(input);
        return new FieldRenderPayload
        {
            FieldKey = input.FieldKey,
            Label = input.Label,
            Hint = input.Hint,
            FieldType = fieldType,
            Required = input.Required,
            Options = input switch
            {
                SelectComponent select => select.Options,
                RadiosComponent radios => radios.Options,
                CheckboxesComponent checkboxes => checkboxes.Options,
                GuidanceChecklistComponent guidance => guidance.Items.Select(i => i.Key).ToList(),
                _ => null
            },
            Value = GetDisplayValue(input, fieldType, savedValues) ?? ResolveDefaultFrom(input, calc) ?? input.Default,
            MinLength = input switch
            {
                TextInputComponent text => text.MinLength,
                TextareaComponent textarea => textarea.MinLength,
                _ => null
            },
            MaxLength = input switch
            {
                TextInputComponent text => text.MaxLength,
                TextareaComponent textarea => textarea.MaxLength,
                _ => null
            },
            Pattern = input switch
            {
                TextInputComponent text => text.Pattern,
                EmailComponent email => email.Pattern,
                _ => null
            },
            Min = input switch
            {
                NumberInputComponent number => number.Min,
                DecimalInputComponent decimalInput => decimalInput.Min,
                SliderComponent slider => slider.Min,
                _ => null
            },
            Max = input switch
            {
                NumberInputComponent number => number.Max,
                DecimalInputComponent decimalInput => decimalInput.Max,
                SliderComponent slider => slider.Max,
                _ => null
            },
            Step = input switch
            {
                SliderComponent slider => slider.Step,
                _ => null
            },
            Suffix = input switch
            {
                SliderComponent slider => slider.Suffix,
                _ => null
            },
            Prefix = input switch
            {
                TextInputComponent text => text.Prefix,
                NumberInputComponent number => number.Prefix,
                DecimalInputComponent decimalInput => decimalInput.Prefix,
                SliderComponent slider => slider.Prefix,
                _ => null
            },
            ConditionalOn = input.ConditionalOn,
            VisibleWhen = input.VisibleWhen,
            ChangeStateKey = input.ChangeStateKey,
            AcceptedFileTypes = input switch
            {
                FileUploadComponent file => file.AcceptedFileTypes,
                _ => null
            },
            MaxSizeBytes = input switch
            {
                FileUploadComponent file => file.MaxSizeBytes,
                _ => null
            },
            GuidanceItems = input switch
            {
                GuidanceChecklistComponent guidance => guidance.Items,
                _ => null
            }
        };
    }

    /// <summary>
    /// The <see cref="FieldRenderPayload.FieldType"/> a host's <c>GovUkComponentRenderer</c>
    /// dispatches rendering on for this input — its registered discriminator (e.g.
    /// <c>"text"</c>, <c>"radio"</c>), from <see cref="ComponentTypeRegistry"/>. Was previously a
    /// hand-written switch over the built-in CLR types (kept exactly in sync with the registry's
    /// own discriminators by luck, not by construction) — meaning a third-party InputComponent
    /// subtype fell through to its <c>_ =&gt; "text"</c> fallback and could never reach a
    /// <c>RegisterField</c> override registered under its own type name, quietly breaking the
    /// extensibility this registry exists to provide.
    /// </summary>
    private static string InputFieldType(InputComponent input) => ComponentTypeRegistry.DiscriminatorFor(input);

    private static object? GetDisplayValue(
        InputComponent input,
        string fieldType,
        Dictionary<string, object?> savedValues)
    {
        var raw = savedValues.TryGetValue(input.FieldKey, out var value) ? value : null;
        if (raw == null)
        {
            return null;
        }

        // Defensive, regardless of THIS field's own declared type: a summary-list/stat-group
        // child echoing a file-upload field's captured value has no requirement to itself be
        // declared as file-upload — GOV.UK's own "check your answers" convention is one row per
        // answer, and nothing in the authoring surface enforces that a summary child's type
        // matches the field it echoes. Confirmed live: a real MCP-authored blueprint declared
        // such a child as plain "text", so fieldType here was "text", the file-upload branch
        // below never ran, and a citizen's own "check your answers" page rendered the raw
        // stored ServiceRequestFileReference JSON instead of the filename. FromFieldValue
        // returns null for every ordinary string/date/boolean/JsonElement-non-object value (a
        // normal answer can never coincidentally look like a file reference), and even an
        // unrelated object-shaped value (a support-system JsonObject payload, say) parses with
        // an empty OriginalFileName rather than a real one — the non-empty check below is what
        // makes this safe to attempt unconditionally rather than gated on fieldType.
        if (ServiceRequestFileReference.FromFieldValue(raw) is { } fileReference
            && !string.IsNullOrEmpty(fileReference.OriginalFileName))
        {
            return fileReference.OriginalFileName;
        }

        if (fieldType == "checkboxlist" || fieldType == "checkboxes")
        {
            var rawString = raw switch
            {
                string stringValue => stringValue,
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
                _ => null
            };

            if (rawString != null)
            {
                raw = string.Join(
                    ", ",
                    rawString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        if (fieldType == "file-upload")
        {
            // Reaching here means the FromFieldValue attempt above already tried and failed to
            // extract a real filename (raw isn't a valid/complete file reference — e.g. nothing
            // uploaded yet) — never fall through to the generic prefix/raw-value return below,
            // which would leak whatever raw shape this is as literal display text.
            return null;
        }

        var prefix = input switch
        {
            TextInputComponent text => text.Prefix,
            NumberInputComponent number => number.Prefix,
            DecimalInputComponent decimalInput => decimalInput.Prefix,
            _ => null
        };

        return !string.IsNullOrEmpty(prefix)
            ? $"{prefix}{raw}"
            : raw;
    }

    /// <summary>
    /// Resolves <see cref="InputComponent.DefaultFrom"/> against the calculation display
    /// overlay — the same already-formatted scope stat-groups and summary-lists read from, so
    /// a "£20"-style gbp format applies here too if the named field declares one. Only ever
    /// called when there's no saved value yet (see <see cref="BuildInputPayload"/>'s value
    /// chain), so a visitor's own submitted choice always overrides this — it's a default, not
    /// a lock.
    /// <para>
    /// A name the display overlay doesn't hold is then looked up in the full calculation scope,
    /// which is where a <c>source: "service"</c> value the host supplied lives (those never reach
    /// the display overlay). A dotted name such as <c>user.email</c> walks into an object-valued
    /// service field. A path that stops short of a scalar resolves to nothing rather than to an
    /// object's text.
    /// </para>
    /// </summary>
    private static string? ResolveDefaultFrom(InputComponent input, CalculationRenderContext? calc)
    {
        if (string.IsNullOrWhiteSpace(input.DefaultFrom) || calc is null)
        {
            return null;
        }

        if (calc.DisplayValues.TryGetValue(input.DefaultFrom, out var value))
        {
            return value?.ToString();
        }

        return TryReadScopePath(calc.Scope, input.DefaultFrom, out var scoped)
            ? StageCalculations.FormatCalculatedValue(scoped, format: null)
            : null;
    }

    private static bool TryReadScopePath(IReadOnlyDictionary<string, object?> scope, string path, out object? value)
    {
        object? current = scope;
        foreach (var segment in path.Split('.'))
        {
            if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(segment, out current))
            {
                continue;
            }

            value = null;
            return false;
        }

        value = current;
        return current is not IReadOnlyDictionary<string, object?>;
    }

    // ─── Gateway helpers ──────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fieldset component contains no renderable fields")]
    private static partial void FieldsetHasNoFields(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary-list component contains no renderable fields")]
    private static partial void SummaryListHasNoFields(ILogger logger);
}
