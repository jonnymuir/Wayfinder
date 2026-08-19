namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// The <see cref="ComponentDescriptor"/> for every component type Wayfinder itself ships.
/// Hand-authored rather than reflected from attributes — deliberately: with a fixed 27-type
/// catalog, an explicit list here is simpler to read, review, and keep correct than a generic
/// attribute-inference engine would be to build and trust, and a third party extending the
/// catalog constructs exactly the same <see cref="ComponentDescriptor"/> shape by hand anyway
/// (see docs/guides/extending-the-component-catalog.md). Every property reference uses
/// <see langword="nameof"/> against the real record property, so a rename anywhere breaks this
/// file at compile time instead of silently drifting — the exact failure mode that let
/// <c>SummaryListComponent.Children</c> and <c>TelComponent</c> go unnoticed for so long.
///
/// <see cref="Component.ShowWhen"/> is deliberately not listed per-type below — it's common to
/// every component (declared on the base record), so an editor UI should offer it once,
/// generically, rather than have it repeated in all 27 property lists here.
/// </summary>
public static class BuiltInComponentDescriptors
{
    // Shared by every InputComponent-derived descriptor — FieldKey/Label/Hint/Required/
    // ConditionalOn/VisibleWhen/Default/DefaultFrom/ChangeStateKey all live on the InputComponent
    // base, not any one derived type. `hasOwnOptions` is true only for select/radio/checkboxlist —
    // it's the one case where "Default value" has a real closed set (the component's own
    // Options), so those three call sites tag Default with "own-options-ref"; every other input
    // type has no useful closed set for its default, so it stays untagged (free text).
    private static IReadOnlyList<ComponentPropertyDescriptor> InputBaseProperties(bool hasOwnOptions = false) =>
    [
        new()
        {
            Key = nameof(InputComponent.FieldKey), Title = "Field key",
            Description = "Unique identifier for this field's captured value.",
            ValueKind = ComponentPropertyValueKind.String, Required = true,
        },
        new()
        {
            Key = nameof(InputComponent.Label), Title = "Label",
            Description = "User-facing label displayed next to the field.",
            ValueKind = ComponentPropertyValueKind.String, Required = true,
        },
        new()
        {
            Key = nameof(InputComponent.Hint), Title = "Hint",
            Description = "Optional helper text displayed below the label.",
            ValueKind = ComponentPropertyValueKind.String,
        },
        new()
        {
            Key = nameof(InputComponent.Required), Title = "Required",
            Description = "Whether this field must be completed before advancing.",
            ValueKind = ComponentPropertyValueKind.Boolean, Editor = "toggle",
        },
        new()
        {
            Key = nameof(InputComponent.ConditionalOn), Title = "Conditional on field",
            Description = "Must be another input field's fieldKey declared in this SAME stage — visibility is " +
                "only ever checked against the current stage's own submitted values, so a fieldKey from a " +
                "different stage (or a typo) leaves this field always hidden.",
            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
        },
        new()
        {
            Key = nameof(InputComponent.VisibleWhen), Title = "Visible when value",
            Description = "The value that makes this field visible when Conditional on field is set — compared " +
                "case-insensitively against the referenced field's submitted value (\"true\"/\"false\" for a " +
                "boolean field, or one of its declared options).",
            ValueKind = ComponentPropertyValueKind.String, Format = "conditional-value-ref",
        },
        new()
        {
            Key = nameof(InputComponent.Default), Title = "Default value",
            Description = "Used when the instance has no saved value for this field yet.",
            ValueKind = ComponentPropertyValueKind.String, Format = hasOwnOptions ? "own-options-ref" : null,
        },
        new()
        {
            Key = nameof(InputComponent.DefaultFrom), Title = "Default from calculation",
            Description = "Names a calculation-scope value to use as this field's default instead of the static " +
                "default. Must be a name declared in this blueprint's calculations.fields.",
            ValueKind = ComponentPropertyValueKind.String, Format = "calculation-ref",
        },
        new()
        {
            Key = nameof(InputComponent.ChangeStateKey), Title = "Change link target stage",
            Description = "When this field appears in a summary-list, the stage its own \"Change\" link " +
                "navigates back to. Must be an existing stage's stageKey.",
            ValueKind = ComponentPropertyValueKind.String, Format = "stage-ref",
        },
    ];

    private static ComponentPropertyDescriptor Prop(
        string key, string title, ComponentPropertyValueKind valueKind,
        string? description = null, string? format = null, string? editor = null,
        IReadOnlyList<string>? allowedValues = null, bool required = false, object? defaultValue = null,
        decimal? minimum = null, decimal? maximum = null, int? minLength = null, int? maxLength = null,
        string? pattern = null) =>
        new()
        {
            Key = key, Title = title, Description = description, ValueKind = valueKind, Format = format,
            Editor = editor, AllowedValues = allowedValues, Required = required, DefaultValue = defaultValue,
            Minimum = minimum, Maximum = maximum, MinLength = minLength, MaxLength = maxLength, Pattern = pattern,
        };

    public static IReadOnlyList<ComponentDescriptor> All { get; } = BuildAll();

    private static IReadOnlyList<ComponentDescriptor> BuildAll()
    {
        var descriptors = new List<ComponentDescriptor>();

        // ── Container components ────────────────────────────────────────────────────────
        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "fieldset", DisplayName = "Fieldset", Category = ComponentCategory.Container,
            Description = "Groups related fields with an optional legend.",
            ClrType = typeof(FieldsetComponent),
            Properties =
            [
                Prop(nameof(FieldsetComponent.Legend), "Legend", ComponentPropertyValueKind.String, "Legend text displayed above the fieldset."),
                Prop(nameof(FieldsetComponent.LegendSize), "Legend size", ComponentPropertyValueKind.String,
                    editor: "select", allowedValues: ["xl", "l", "m", "s"]),
            ],
            Containment = ComponentContainment.ChildList(nameof(FieldsetComponent.Children)),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "accordion", DisplayName = "Accordion", Category = ComponentCategory.Container,
            Description = "Collapsible sections, each with their own child components.",
            ClrType = typeof(AccordionComponent),
            Containment = ComponentContainment.NamedSections(
                nameof(AccordionComponent.Sections), nameof(AccordionSection.Children)),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "panel", DisplayName = "Panel", Category = ComponentCategory.Content,
            Description = "Confirmation-style panel, typically the heading of an outcome stage.",
            ClrType = typeof(PanelComponent),
            Properties = [Prop(nameof(PanelComponent.Heading), "Heading", ComponentPropertyValueKind.String, required: true)],
        });

        // ── Content components ───────────────────────────────────────────────────────────
        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "body", DisplayName = "Body text", Category = ComponentCategory.Content,
            ClrType = typeof(BodyComponent),
            Properties = [Prop(nameof(BodyComponent.Content), "Content", ComponentPropertyValueKind.String, editor: "textarea", required: true)],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "heading", DisplayName = "Heading", Category = ComponentCategory.Content,
            ClrType = typeof(HeadingComponent),
            Properties =
            [
                Prop(nameof(HeadingComponent.Level), "Level", ComponentPropertyValueKind.Integer, minimum: 1, maximum: 6, defaultValue: 2),
                Prop(nameof(HeadingComponent.Content), "Content", ComponentPropertyValueKind.String, required: true),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "inset-text", DisplayName = "Inset text", Category = ComponentCategory.Content,
            Description = "Highlights important content in an inset box.",
            ClrType = typeof(InsetTextComponent),
            Properties = [Prop(nameof(InsetTextComponent.Content), "Content", ComponentPropertyValueKind.String, editor: "textarea", required: true)],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "warning-text", DisplayName = "Warning text", Category = ComponentCategory.Content,
            Description = "Displays a warning message with an exclamation icon.",
            ClrType = typeof(WarningTextComponent),
            Properties = [Prop(nameof(WarningTextComponent.Content), "Content", ComponentPropertyValueKind.String, editor: "textarea", required: true)],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "details", DisplayName = "Details", Category = ComponentCategory.Content,
            Description = "Expandable/collapsible section.",
            ClrType = typeof(DetailsComponent),
            Properties =
            [
                Prop(nameof(DetailsComponent.Heading), "Summary (clickable heading)", ComponentPropertyValueKind.String, required: true),
                Prop(nameof(DetailsComponent.Content), "Content revealed when expanded", ComponentPropertyValueKind.String, editor: "textarea", required: true),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "notification-banner", DisplayName = "Notification banner", Category = ComponentCategory.Content,
            ClrType = typeof(NotificationBannerComponent),
            Properties =
            [
                Prop(nameof(NotificationBannerComponent.BannerType), "Banner type", ComponentPropertyValueKind.String,
                    editor: "select", allowedValues: ["info", "success", "warning"], defaultValue: "info"),
                Prop(nameof(NotificationBannerComponent.Heading), "Heading", ComponentPropertyValueKind.String, required: true),
                Prop(nameof(NotificationBannerComponent.Content), "Content", ComponentPropertyValueKind.String, editor: "textarea", required: true),
            ],
        });

        // ── Input components ─────────────────────────────────────────────────────────────
        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "text", DisplayName = "Text input", Category = ComponentCategory.Input,
            ClrType = typeof(TextInputComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(TextInputComponent.MinLength), "Minimum length", ComponentPropertyValueKind.Integer),
                Prop(nameof(TextInputComponent.MaxLength), "Maximum length", ComponentPropertyValueKind.Integer),
                Prop(nameof(TextInputComponent.Pattern), "Pattern (regex)", ComponentPropertyValueKind.String, format: "pattern"),
                Prop(nameof(TextInputComponent.Prefix), "Prefix", ComponentPropertyValueKind.String, "e.g. \"£\"."),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "number", DisplayName = "Number input", Category = ComponentCategory.Input,
            Description = "Integer values.",
            ClrType = typeof(NumberInputComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(NumberInputComponent.Min), "Minimum value", ComponentPropertyValueKind.Number),
                Prop(nameof(NumberInputComponent.Max), "Maximum value", ComponentPropertyValueKind.Number),
                Prop(nameof(NumberInputComponent.Prefix), "Prefix", ComponentPropertyValueKind.String, "e.g. \"£\"."),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "decimal", DisplayName = "Decimal input", Category = ComponentCategory.Input,
            Description = "Floating-point values.",
            ClrType = typeof(DecimalInputComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(DecimalInputComponent.Min), "Minimum value", ComponentPropertyValueKind.Number),
                Prop(nameof(DecimalInputComponent.Max), "Maximum value", ComponentPropertyValueKind.Number),
                Prop(nameof(DecimalInputComponent.Prefix), "Prefix", ComponentPropertyValueKind.String, "e.g. \"£\"."),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "select", DisplayName = "Select dropdown", Category = ComponentCategory.Input,
            ClrType = typeof(SelectComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(hasOwnOptions: true),
                Prop(nameof(SelectComponent.Options), "Options", ComponentPropertyValueKind.StringArray, required: true),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "radio", DisplayName = "Radios", Category = ComponentCategory.Input,
            Description = "Radio button group with optional conditional child components.",
            ClrType = typeof(RadiosComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(hasOwnOptions: true),
                Prop(nameof(RadiosComponent.Options), "Options", ComponentPropertyValueKind.StringArray, required: true),
            ],
            Containment = ComponentContainment.KeyedChildren(
                nameof(RadiosComponent.ConditionalChildren), nameof(RadiosComponent.Options)),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "checkboxlist", DisplayName = "Checkboxes", Category = ComponentCategory.Input,
            Description = "Checkbox group with optional conditional child components.",
            ClrType = typeof(CheckboxesComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(hasOwnOptions: true),
                Prop(nameof(CheckboxesComponent.Options), "Options", ComponentPropertyValueKind.StringArray, required: true),
            ],
            Containment = ComponentContainment.KeyedChildren(
                nameof(CheckboxesComponent.ConditionalChildren), nameof(CheckboxesComponent.Options)),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "date", DisplayName = "Date input", Category = ComponentCategory.Input,
            ClrType = typeof(DateInputComponent), IsInput = true,
            Properties = InputBaseProperties(),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "email", DisplayName = "Email input", Category = ComponentCategory.Input,
            ClrType = typeof(EmailComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(EmailComponent.Pattern), "Pattern (regex)", ComponentPropertyValueKind.String, format: "pattern"),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "textarea", DisplayName = "Textarea", Category = ComponentCategory.Input,
            Description = "Multi-line text input.",
            ClrType = typeof(TextareaComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(TextareaComponent.MinLength), "Minimum length", ComponentPropertyValueKind.Integer),
                Prop(nameof(TextareaComponent.MaxLength), "Maximum length", ComponentPropertyValueKind.Integer),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "boolean", DisplayName = "Boolean (single checkbox)", Category = ComponentCategory.Input,
            ClrType = typeof(BooleanComponent), IsInput = true,
            Properties = InputBaseProperties(),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "file-upload", DisplayName = "File upload", Category = ComponentCategory.Input,
            Description = "A single named document slot — one component per document a blueprint needs.",
            ClrType = typeof(FileUploadComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(FileUploadComponent.AcceptedFileTypes), "Accepted file types", ComponentPropertyValueKind.StringArray,
                    "e.g. [\".pdf\", \".jpg\", \".png\"]. Omit for no restriction."),
                Prop(nameof(FileUploadComponent.MaxSizeBytes), "Maximum size (bytes)", ComponentPropertyValueKind.Integer,
                    "Falls back to the platform's own default limit if omitted."),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "slider", DisplayName = "Slider", Category = ComponentCategory.Input,
            Description = "Range slider input; submits like a number field.",
            ClrType = typeof(SliderComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(SliderComponent.Min), "Minimum value", ComponentPropertyValueKind.Number),
                Prop(nameof(SliderComponent.Max), "Maximum value", ComponentPropertyValueKind.Number),
                Prop(nameof(SliderComponent.Step), "Step", ComponentPropertyValueKind.Number, "Interval between selectable values, e.g. 0.5."),
                Prop(nameof(SliderComponent.Prefix), "Prefix", ComponentPropertyValueKind.String, "e.g. \"£\"."),
                Prop(nameof(SliderComponent.Suffix), "Suffix", ComponentPropertyValueKind.String, "e.g. \"%\"."),
            ],
        });

        // ── Data-display components ──────────────────────────────────────────────────────
        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "stat-group", DisplayName = "Statistic group", Category = ComponentCategory.DataDisplay,
            Description = "A group of headline statistic tiles, resolved from instance/calculated field values.",
            ClrType = typeof(StatGroupComponent),
            Properties =
            [
                Prop(nameof(StatGroupComponent.Title), "Title", ComponentPropertyValueKind.String),
                new()
                {
                    Key = nameof(StatGroupComponent.Items), Title = "Statistic tiles",
                    ValueKind = ComponentPropertyValueKind.Array, Required = true,
                    Items = new ComponentPropertyDescriptor
                    {
                        Key = "item", Title = "Statistic tile", ValueKind = ComponentPropertyValueKind.Object,
                        Properties =
                        [
                            Prop(nameof(StatItemDefinition.Label), "Label", ComponentPropertyValueKind.String, required: true),
                            Prop(nameof(StatItemDefinition.FieldKey), "Field key", ComponentPropertyValueKind.String,
                                "The instance/calculated field this tile's value is read from. Must be a " +
                                "calculations.fields name or an input field's fieldKey captured anywhere in the " +
                                "blueprint (not just this stage).", format: "field-or-calc-ref", required: true),
                            Prop(nameof(StatItemDefinition.Qualifier), "Qualifier", ComponentPropertyValueKind.String, "e.g. \"a year, for life\"."),
                            Prop(nameof(StatItemDefinition.Emphasis), "Emphasis", ComponentPropertyValueKind.Boolean, editor: "toggle"),
                        ],
                    },
                },
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "chart", DisplayName = "Chart", Category = ComponentCategory.DataDisplay,
            Description = "Declarative chart bound to a calculation series.",
            ClrType = typeof(ChartComponent),
            Properties =
            [
                Prop(nameof(ChartComponent.Title), "Title", ComponentPropertyValueKind.String),
                Prop(nameof(ChartComponent.Kind), "Kind", ComponentPropertyValueKind.String,
                    editor: "select", allowedValues: ["stacked-bar"], defaultValue: "stacked-bar"),
                Prop(nameof(ChartComponent.Series), "Series", ComponentPropertyValueKind.String,
                    "Name of the calculation series supplying the rows.", required: true),
                Prop(nameof(ChartComponent.X), "X axis column", ComponentPropertyValueKind.String,
                    "Series column used for the x axis, typically the loop variable.", required: true),
                new()
                {
                    Key = nameof(ChartComponent.Bands), Title = "Stacked bands",
                    ValueKind = ComponentPropertyValueKind.Array, Required = true,
                    Items = new ComponentPropertyDescriptor
                    {
                        Key = "band", Title = "Band", ValueKind = ComponentPropertyValueKind.Object,
                        Properties =
                        [
                            Prop(nameof(ChartBand.Key), "Series column", ComponentPropertyValueKind.String, required: true),
                            Prop(nameof(ChartBand.Label), "Legend label", ComponentPropertyValueKind.String, required: true),
                            Prop(nameof(ChartBand.Color), "Colour", ComponentPropertyValueKind.String,
                                description: "Optional hex colour; assigned from the categorical palette in band order if omitted.",
                                format: "color"),
                        ],
                    },
                },
                Prop(nameof(ChartComponent.XLabelEvery), "Label interval", ComponentPropertyValueKind.Integer,
                    "e.g. 5 → label every 5th value.", defaultValue: 5),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "bulk-data-review", DisplayName = "Bulk data review", Category = ComponentCategory.DataDisplay,
            Description = "Paginated, only-what-needs-attention review UI over a bulk-dataset-ingest action's dataset. See docs/guides/bulk-data-review.md.",
            ClrType = typeof(BulkDataReviewComponent),
            Properties =
            [
                Prop(nameof(BulkDataReviewComponent.Title), "Title", ComponentPropertyValueKind.String),
                Prop(nameof(BulkDataReviewComponent.DatasetIdField), "Dataset id field", ComponentPropertyValueKind.String,
                    "Must match a bulk-dataset-ingest action's own datasetIdField param.", format: "field-ref", required: true),
                Prop(nameof(BulkDataReviewComponent.PageSize), "Rows per page", ComponentPropertyValueKind.Integer, defaultValue: 20),
                Prop(nameof(BulkDataReviewComponent.SyncedLabel), "Synced label", ComponentPropertyValueKind.String,
                    "Shown when nothing has changed since the last check. Defaults to \"Synced\"."),
                Prop(nameof(BulkDataReviewComponent.PendingLabel), "Pending label", ComponentPropertyValueKind.String,
                    "Shown when something has changed since the last check — service-specific wording (e.g. \"Pending resubmission\"). Defaults to \"Needs resubmission\"."),
                Prop(nameof(BulkDataReviewComponent.SinceLabel), "Since label", ComponentPropertyValueKind.String,
                    "The reference-point phrase used in both the sync-status line and the discard-changes warning, e.g. \"since the file was last submitted\". Defaults to \"since the file was last checked\"."),
            ],
        });

        // ── Flow-control / service-blueprint components ─────────────────────────────────
        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "waiting", DisplayName = "Waiting", Category = ComponentCategory.FlowControl,
            Description = "Displays a message while the blueprint is paused pending external processing. Used at Join gateways.",
            ClrType = typeof(WaitingComponent),
            Properties =
            [
                Prop(nameof(WaitingComponent.Content), "Message", ComponentPropertyValueKind.String, editor: "textarea", required: true),
                Prop(nameof(WaitingComponent.ExpectedWaitSeconds), "Expected wait (seconds)", ComponentPropertyValueKind.Integer),
                Prop(nameof(WaitingComponent.PollIntervalMs), "Poll interval (ms)", ComponentPropertyValueKind.Integer, defaultValue: 3000),
                Prop(nameof(WaitingComponent.AllowDefer), "Allow \"leave and return later\"", ComponentPropertyValueKind.Boolean,
                    editor: "toggle", defaultValue: true),
                Prop(nameof(WaitingComponent.DeferMessage), "Defer message", ComponentPropertyValueKind.String),
            ],
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "summary-list", DisplayName = "Summary list", Category = ComponentCategory.DataDisplay,
            Description = "Displays a list of field values with optional \"Change\" links — GOV.UK's check-your-answers pattern.",
            ClrType = typeof(SummaryListComponent),
            Properties =
            [
                Prop(nameof(SummaryListComponent.Title), "Title", ComponentPropertyValueKind.String),
                Prop(nameof(SummaryListComponent.ChangeStateKey), "Change link target stage", ComponentPropertyValueKind.String, format: "stage-ref"),
            ],
            Containment = ComponentContainment.ChildList(nameof(SummaryListComponent.Children)),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "task-list", DisplayName = "Task list", Category = ComponentCategory.DataDisplay,
            Description = "Displays a list of blueprint tasks grouped by section — auto-generated from stages if sections is omitted.",
            ClrType = typeof(TaskListComponent),
        });

        descriptors.Add(new ComponentDescriptor
        {
            Discriminator = "guidance-checklist", DisplayName = "Guidance checklist", Category = ComponentCategory.Input,
            Description = "Linked guidance articles, each with its own acknowledgement checkbox — Required means every item must be acknowledged.",
            ClrType = typeof(GuidanceChecklistComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                new()
                {
                    Key = nameof(GuidanceChecklistComponent.Items), Title = "Guidance items",
                    ValueKind = ComponentPropertyValueKind.Array, Required = true,
                    Items = new ComponentPropertyDescriptor
                    {
                        Key = "item", Title = "Guidance item", ValueKind = ComponentPropertyValueKind.Object,
                        Properties =
                        [
                            Prop(nameof(GuidanceChecklistItem.Key), "Key", ComponentPropertyValueKind.String,
                                "Stable identifier posted when this item is acknowledged.", required: true),
                            Prop(nameof(GuidanceChecklistItem.Label), "Label", ComponentPropertyValueKind.String, required: true),
                            Prop(nameof(GuidanceChecklistItem.Href), "Link URL", ComponentPropertyValueKind.String, format: "uri", required: true),
                        ],
                    },
                },
            ],
        });

        return descriptors;
    }
}
