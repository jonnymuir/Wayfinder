namespace Wayfinder.Models.ServiceDesign;

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
    /// <summary>How the value is shown: null or "text" for a figure, "map" for a read-only map of a point.</summary>
    public string? Display { get; init; }

    public string? Width { get; init; }
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
