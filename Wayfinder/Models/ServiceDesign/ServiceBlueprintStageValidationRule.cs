using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// A declarative, cross-field business rule that must hold before a <see cref="StageDefinition"/>
/// can advance — the engine-native alternative to a host overriding
/// <c>ProcessManagerEngine.ValidateAdvance</c> with bespoke C#. Both <see cref="When"/> and
/// <see cref="Rule"/> are plain expressions in the blueprint's own calculation language
/// (<c>docs/guides/calculation-language.md</c>), evaluated against the same scope
/// <c>calculations.fields</c> and every component's <c>showWhen</c> already use — which spans
/// every stage the instance has reached, not just this one, so a rule may reference a field
/// captured on an earlier stage without any extra wiring.
/// </summary>
/// <param name="Code">
/// Machine-readable rule identifier, surfaced on the resulting <c>ServiceRequestProblem.Code</c>
/// (e.g. so a host can route on it, the way the C# escape hatch this replaces did).
/// </param>
/// <param name="When">
/// Optional guard expression; must evaluate to a boolean. When present and false, this rule is
/// skipped entirely — no failure, not evaluated. Kept as a separate expression rather than folded
/// into <see cref="Rule"/> as an implication (<c>not (when) or rule</c>) deliberately: an author
/// only ever writes an affirmative "what must hold", never has to hand-negate an applicability
/// condition (a common, hard-to-spot class of authoring bug), and design-time tooling gets a
/// distinct seam to reason about "does this rule even apply" separately from "is it satisfied".
/// Omit for a rule that always applies.
/// </param>
/// <param name="Rule">
/// Expression that must evaluate to <c>true</c> for the stage to be allowed to advance. Positive
/// phrasing only — there is no separate "failWhen" — matching the same convention as
/// <c>showWhen</c>.
/// </param>
/// <param name="Field">
/// Optional fieldKey (on this stage) to attach a failure to, GDS error-summary style. Omit for a
/// stage-level problem not tied to one field.
/// </param>
/// <param name="Message">Human-readable explanation shown to the user when this rule fails.</param>
public sealed record ServiceBlueprintStageValidationRule(
    string Code,
    string Rule,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? When = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);
