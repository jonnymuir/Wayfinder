using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public record ServiceBlueprintRouteDefinition
{
    public string Id { get; init; } = "";

    public string Target { get; init; } = "";

    public string Trigger { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiresRole { get; init; }

    /// <summary>
    /// Optional visibility expression, in the same calculation language and evaluated with the
    /// same fail-open bias as <see cref="Components.Component.ShowWhen"/> — when it evaluates to
    /// false this route is excluded from the stage's available actions entirely, not merely
    /// disabled or blocked-with-an-error. Only evaluated for a stage's own routes; it has no
    /// effect on a gateway's own routes (a Split gateway always fans out to every route
    /// regardless, and a Join gateway selects by matching the arriving trigger, not by this) —
    /// <c>ServiceBlueprintAuthoringService.Validate</c> flags a <c>ShowWhen</c> set there as a
    /// diagnostic rather than let it silently do nothing.
    ///
    /// Use this, not a <see cref="ServiceBlueprintStageValidationRule"/> scoped via
    /// <see cref="ServiceBlueprintStageValidationRule.Actions"/>, when a stage has genuinely
    /// different exits and exactly one should be *offered* for a given state of the data — e.g.
    /// "send to insurer" vs. "continue" depending on whether a file was attached. Reach for a
    /// scoped validation rule instead when the exit should always stay offered but needs to be
    /// *blocked with an explanation* until some condition holds — the two are different UX, not
    /// interchangeable spellings of the same thing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShowWhen { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionDefinition>? Actions { get; init; }
}
