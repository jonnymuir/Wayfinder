using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// Standard API response envelope for service request operations.
/// </summary>
public record ServiceRequestResponseEnvelope
{
    /// <summary>
    /// Gets the service request identifier.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Gets the response stage indicating what the client should do next.
    /// Valid values: render, defer, complete, error.
    /// </summary>
    public required string ResponseState { get; init; }

    /// <summary>
    /// Gets the current stage version for optimistic concurrency control.
    /// </summary>
    public required int StateVersion { get; init; }

    /// <summary>
    /// Gets the correlation identifier for tracking related service requests.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Gets the server UTC timestamp.
    /// </summary>
    public required DateTimeOffset ServerTimeUtc { get; init; }

    /// <summary>
    /// Gets the recommended polling interval in milliseconds (nullable).
    /// Only present when ResponseState is "defer".
    /// </summary>
    public int? PollAfterMs { get; init; }

    /// <summary>
    /// Gets the render payload for UI presentation (nullable).
    /// Only present when ResponseState is "render".
    /// </summary>
    public StepContent? Render { get; init; }

    /// <summary>
    /// Gets the instance policy from the service blueprint.
    /// Valid values: "single", "multiple", "prompt".
    /// </summary>
    public string? RequestPolicy { get; init; }

    /// <summary>
    /// Gets the list of validation or error problems.
    /// </summary>
    public IReadOnlyList<ServiceRequestProblem> Problems { get; init; } = Array.Empty<ServiceRequestProblem>();
}

/// <summary>
/// Render payload for UI presentation.
/// </summary>
public record StepContent
{
    /// <summary>
    /// Gets the step type for UI rendering (question, check-answers, confirmation, status-timeline, task-list).
    /// </summary>
    public required string StepType { get; init; }

    /// <summary>
    /// Gets the stage display name.
    /// </summary>
    public required string StateDisplayName { get; init; }

    /// <summary>
    /// Gets the GDS components to render at this step.
    /// </summary>
    public required IReadOnlyList<ComponentRenderPayload> Components { get; init; }

    /// <summary>
    /// Gets the available actions the user can take.
    /// </summary>
    public required IReadOnlyList<ServiceRequestAction> AvailableActions { get; init; }

    /// <summary>
    /// Host-supplied structured display data for this step (nullable).
    /// Populated by the engine's render-data hook; keyed sections are resolved into
    /// "interactive" components via their DataKey. Display data only — never instructions.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? Data { get; init; }
}

/// <summary>
/// Runtime representation of a GDS component, with field values pre-populated from the service request.
/// Sent from the engine to the Core controller, which passes it to the view.
/// </summary>
public record ComponentRenderPayload
{
    /// <summary>The GDS component type (e.g. "fieldset", "summary-list", "panel", "body", "heading").</summary>
    public string Type { get; init; } = "fieldset";

    // Fieldset
    /// <summary>The fieldset legend text (overrides the field group DisplayName).</summary>
    public string? Legend { get; init; }
    /// <summary>Legend size: "xl" | "l" | "m" | "s".</summary>
    public string? LegendSize { get; init; }
    /// <summary>Fields to render within this component (used by fieldset and summary-list).</summary>
    public IReadOnlyList<FieldRenderPayload> Fields { get; init; } = Array.Empty<FieldRenderPayload>();

    // Summary-list
    /// <summary>Heading above the summary list (overrides the field group DisplayName).</summary>
    public string? Title { get; init; }
    /// <summary>The stage key the "Change" links navigate to (summary-list only).</summary>
    public string? SourceStateKey { get; init; }

    // Content types
    /// <summary>
    /// Pre-sanitized HTML; safe for <c>@Html.Raw</c>.
    /// Producers MUST route content through <c>IServiceContentSanitizer</c> before populating this property.
    /// </summary>
    public string? Content { get; init; }
    /// <summary>Panel title, notification banner heading, or details summary text.</summary>
    public string? Heading { get; init; }
    /// <summary>Banner type for notification-banner: "info" | "success" | "warning".</summary>
    public string? BannerType { get; init; }
    /// <summary>Heading level 1-6 for "heading" type components.</summary>
    public int? Level { get; init; }

    // Task list
    /// <summary>Task sections for task-list components.</summary>
    public IReadOnlyList<TaskSectionPayload>? TaskSections { get; init; }

    // Accordion
    /// <summary>Accordion sections for accordion components.</summary>
    public IReadOnlyList<AccordionSectionPayload>? AccordionSections { get; init; }

    // Stat group
    /// <summary>Resolved statistic tiles for "stat-group" components.</summary>
    public IReadOnlyList<StatItem>? Stats { get; init; }

    // Chart
    /// <summary>Resolved chart model JSON for "chart" components: kind, x, bands, rows.</summary>
    public string? ChartJson { get; init; }

    // Live visibility
    /// <summary>
    /// The component's showWhen expression (when declared) — emitted as a data attribute
    /// so the live-form runtime can re-evaluate visibility as inputs change.
    /// </summary>
    public string? ShowWhen { get; init; }
    /// <summary>Server-evaluated result of ShowWhen: true renders the component hidden.</summary>
    public bool Hidden { get; init; }

    // Waiting
    /// <summary>Expected wait time in seconds for "waiting" components.</summary>
    public int? ExpectedWaitSeconds { get; init; }
    /// <summary>Polling interval in milliseconds for "waiting" components.</summary>
    public int? PollIntervalMs { get; init; }
    /// <summary>Allow deferral of waiting for "waiting" components.</summary>
    public bool? AllowDefer { get; init; }
    /// <summary>Message to show if the user defers the wait (for "waiting" components).</summary>
    public string? DeferMessage { get; init; }

    // Bulk data review
    /// <summary>
    /// Resolved value of the "bulk-data-review" component's own DatasetIdField — null/empty
    /// before anything's been ingested yet (see docs/guides/bulk-data-review.md). The renderer
    /// treats an empty value as "nothing to review yet" rather than an error.
    /// </summary>
    public string? DatasetId { get; init; }
    /// <summary>How many attention-rows to show per page for "bulk-data-review" components.</summary>
    public int? PageSize { get; init; }
    /// <summary>
    /// Host-supplied base URL for this dataset's REST endpoints (summary/rows/correct/download) —
    /// a host routing concern the engine itself has no opinion on, the same reasoning
    /// <c>WithFileDownloadUrls</c> already applies to file-upload fields. Null until a host's own
    /// post-processing step fills it in.
    /// </summary>
    public string? BulkDatasetApiUrl { get; init; }
    /// <summary>Raw passthrough of <c>BulkDataReviewComponent.SyncedLabel</c> — null/empty means
    /// the renderer applies its own default ("Synced"). See docs/guides/bulk-data-review.md.</summary>
    public string? SyncedLabel { get; init; }
    /// <summary>Raw passthrough of <c>BulkDataReviewComponent.PendingLabel</c> — null/empty means
    /// the renderer applies its own default ("Needs resubmission").</summary>
    public string? PendingLabel { get; init; }
    /// <summary>Raw passthrough of <c>BulkDataReviewComponent.SinceLabel</c> — null/empty means
    /// the renderer applies its own default ("since the file was last checked").</summary>
    public string? SinceLabel { get; init; }

    /// <summary>
    /// Computed display name for this component — returns the most semantically appropriate heading property
    /// based on component type. Used by views that iterate over components and render section headings.
    /// </summary>
    public string DisplayName => Type switch
    {
        "fieldset" => Legend ?? "",
        "summary-list" => Title ?? "",
        _ => Heading ?? ""
    };
}

/// <summary>A resolved statistic tile within a rendered stat-group component.</summary>
public record StatItem
{
    /// <summary>Short label above the value (e.g. "DB pension").</summary>
    public string Label { get; init; } = "";
    /// <summary>Field key the value was resolved from — stable hook for client-side updates.</summary>
    public string FieldKey { get; init; } = "";
    /// <summary>Resolved display value (e.g. "£16,400").</summary>
    public string? Value { get; init; }
    /// <summary>Qualifier text below the value (e.g. "a year, for life").</summary>
    public string? Qualifier { get; init; }
    /// <summary>Whether to render this tile with visual emphasis.</summary>
    public bool Emphasis { get; init; }
}

/// <summary>A section within a rendered task-list component.</summary>
public record TaskSectionPayload
{
    /// <summary>The task section heading.</summary>
    public string Heading { get; init; } = "";
    /// <summary>The tasks within this section.</summary>
    public IReadOnlyList<TaskItemPayload> Tasks { get; init; } = Array.Empty<TaskItemPayload>();
}

/// <summary>A single rendered task item within a task-list section.</summary>
public record TaskItemPayload
{
    /// <summary>The task label shown to the user.</summary>
    public string Label { get; init; } = "";
    /// <summary>The resolved URL for this task.</summary>
    public string? Href { get; init; }
    /// <summary>Task status: "not-started" | "in-progress" | "completed" | "cannot-start".</summary>
    public string Status { get; init; } = "not-started";
}

/// <summary>A rendered accordion section with populated fields.</summary>
public record AccordionSectionPayload
{
    /// <summary>The accordion section heading.</summary>
    public string Heading { get; init; } = "";
    /// <summary>Optional summary text shown beneath the heading when collapsed.</summary>
    public string? Summary { get; init; }
    /// <summary>
    /// Pre-sanitized HTML; safe for <c>@Html.Raw</c>.
    /// Producers MUST route content through <c>IServiceContentSanitizer</c> before populating this property.
    /// </summary>
    public string? Content { get; init; }
    /// <summary>Fields rendered within this accordion section.</summary>
    public IReadOnlyList<FieldRenderPayload> Fields { get; init; } = Array.Empty<FieldRenderPayload>();
}

/// <summary>
/// Individual field render payload.
/// </summary>
public record FieldRenderPayload
{
    /// <summary>
    /// Gets the field key.
    /// </summary>
    public required string FieldKey { get; init; }

    /// <summary>
    /// Gets the field label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the hint text.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Gets the field type (text, email, number, select, etc.).
    /// </summary>
    public required string FieldType { get; init; }

    /// <summary>
    /// Gets whether the field is required.
    /// </summary>
    public required bool Required { get; init; }

    /// <summary>
    /// Gets the current field value (nullable).
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Gets the default value to pre-populate the field (nullable).
    /// Takes precedence over user-submitted values.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Gets whether the field is read-only and cannot be edited by the user.
    /// Read-only fields are rendered as disabled inputs or plain text.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Gets the options for select/radio fields (nullable).
    /// </summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>
    /// Gets the currency/unit prefix displayed before the input (e.g., "£").
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// A URL at which a <c>file-upload</c> field's already-uploaded file can be opened, making a
    /// read-only summary row render as a real link rather than a filename in plain text.
    ///
    /// Deliberately <em>never</em> set by the engine: the engine only ever holds an opaque
    /// <see cref="ServiceRequestFileReference"/> and has no idea what URL space its host serves
    /// files from (see <c>IServiceRequestFileStorage</c> — the host owns storage *and* routing).
    /// A host that exposes a download route enriches the payload with it before rendering; one
    /// that doesn't simply leaves this null and gets today's plain-filename display. That split is
    /// why viewing an uploaded file needs no new component type: it's a host rendering concern
    /// attached to the existing <c>file-upload</c> field, not a new blueprint-authored concept.
    /// </summary>
    public string? FileUrl { get; init; }

    /// <summary>
    /// For radios/checkboxes: sub-fields revealed when the parent option is selected.
    /// Key is the option value; value is the list of fields shown when that option is active.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FieldRenderPayload>>? ConditionalFields { get; init; }

    /// <summary>
    /// Gets the minimum character length for text/textarea fields (nullable).
    /// </summary>
    public int? MinLength { get; init; }

    /// <summary>
    /// Gets the maximum character length for text/textarea fields (nullable).
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Gets the HTML5 pattern (regex) attribute value for text/email fields (nullable).
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Gets the minimum value for number fields (nullable).
    /// </summary>
    public decimal? Min { get; init; }

    /// <summary>
    /// Gets the maximum value for number fields (nullable).
    /// </summary>
    public decimal? Max { get; init; }

    /// <summary>
    /// Gets the step between selectable values for slider fields (nullable).
    /// </summary>
    public decimal? Step { get; init; }

    /// <summary>
    /// Gets the unit suffix displayed after the value (e.g., "%").
    /// </summary>
    public string? Suffix { get; init; }

    /// <summary>
    /// The field key this field depends on for visibility. When set, this field is only
    /// shown when the dependency field's value matches <see cref="VisibleWhen"/>.
    /// </summary>
    public string? ConditionalOn { get; init; }

    /// <summary>
    /// The value that makes this field visible when <see cref="ConditionalOn"/> is set.
    /// </summary>
    public string? VisibleWhen { get; init; }

    /// <summary>
    /// Gets the content to render for non-input content field types
    /// (inset-text, warning-text, details, notification-banner).
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// For a summary-list row: the stage key this row's own "Change" link navigates back to,
    /// overriding the parent summary-list's <see cref="ComponentRenderPayload.SourceStateKey"/>.
    /// Null outside a summary-list context.
    /// </summary>
    public string? ChangeStateKey { get; init; }

    /// <summary>
    /// File extensions accepted for a <c>file-upload</c> field (the HTML <c>accept</c>
    /// attribute), e.g. [".pdf", ".jpg"]. Null means no restriction.
    /// </summary>
    public IReadOnlyList<string>? AcceptedFileTypes { get; init; }

    /// <summary>
    /// Maximum upload size in bytes for a <c>file-upload</c> field, enforced server-side on
    /// POST. Null falls back to <c>ServiceRequestPageController.DefaultMaxFileSizeBytes</c>.
    /// </summary>
    public long? MaxSizeBytes { get; init; }

    /// <summary>
    /// The guidance items for a <c>guidance-checklist</c> field — <see cref="Options"/> only
    /// carries each item's key (needed for the required-all-acknowledged validation), so the
    /// label and link to render come from here instead.
    /// </summary>
    public IReadOnlyList<GuidanceChecklistItem>? GuidanceItems { get; init; }
}

/// <summary>
/// Action available to the user on the current service request.
/// </summary>
public record ServiceRequestAction
{
    /// <summary>
    /// Gets the action key.
    /// </summary>
    public required string ActionKey { get; init; }

    /// <summary>
    /// Gets the action label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the action style.
    /// Valid values: primary, secondary, destructive.
    /// </summary>
    public required string Style { get; init; }
}

/// <summary>
/// Validation or error problem.
/// </summary>
public record ServiceRequestProblem
{
    /// <summary>
    /// Gets the field key this problem relates to.
    /// </summary>
    public required string FieldKey { get; init; }

    /// <summary>
    /// Gets the error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the error code.
    /// </summary>
    public required string Code { get; init; }
}
