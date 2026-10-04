using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;
using static Wayfinder.Engine.Services.FieldValueMerge;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Evaluates a blueprint's calculations, <c>showWhen</c> expressions and stage validation rules
/// against an instance's field values. <paramref name="resolveServiceInputs"/> is the host's supplier
/// of <c>source: "service"</c> values.
/// </summary>
internal sealed partial class StageCalculations(
    ILogger logger,
    InstanceRepository instances,
    Func<ServiceRequest, ServiceBlueprint, StageDefinition, IReadOnlyDictionary<string, object?>?> resolveServiceInputs)
{
    private readonly CalculationEvaluator _calculationEvaluator = new();

    private static readonly JsonSerializerOptions LiveModelJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public CalculationRenderContext? EvaluateDefinitionCalculations(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage)
    {
        if (definition.Calculations is null)
        {
            return null;
        }

        try
        {
            var serviceInputs = resolveServiceInputs(instance, definition, stage);
            var scope = CalculationScopeBuilder.Build(definition, instance.FieldValues, serviceInputs);
            var result = _calculationEvaluator.Evaluate(definition.Calculations, scope);

            // Full scope (inputs + calculated fields) for showWhen evaluation.
            var fullScope = new Dictionary<string, object?>(scope, StringComparer.Ordinal);
            foreach (var (name, value) in result.Fields)
            {
                fullScope[name] = value;
            }

            // Display overlay: saved values, then calculated fields formatted per their
            // declared format — this is what stat-groups and summary-lists resolve from.
            var display = new Dictionary<string, object?>(instance.FieldValues, StringComparer.Ordinal);
            foreach (var (name, value) in result.Fields)
            {
                var format = definition.Calculations.Fields.TryGetValue(name, out var field) ? field.Format : null;
                display[name] = FormatCalculatedValue(value, format);
            }

            // Last computed result is kept on the instance so a composed caller (e.g. the
            // simulation runner, which builds this engine rather than subclassing it) can
            // read raw calculated values without duplicating evaluation itself.
            instances.Save(instance with { LastCalculationResult = result }, instance.UserId);

            return new CalculationRenderContext(definition.Calculations, fullScope, result, display);
        }
        catch (CalculationException exception)
        {
            CalculationFailed(logger, exception, definition.DefinitionKey, stage.StageKey);
            return null;
        }
    }

    public static string? FormatCalculatedValue(object? value, string? format) => value switch
    {
        null => null,
        decimal d when string.Equals(format, "gbp", StringComparison.OrdinalIgnoreCase) =>
            string.Create(
                System.Globalization.CultureInfo.GetCultureInfo("en-GB"),
                $"£{Math.Round(d, 0, MidpointRounding.AwayFromZero):N0}"),
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString()
    };

    public static JsonObject BuildLiveModel(ServiceBlueprint definition, CalculationRenderContext calc)
    {
        var inputTypes = new JsonObject();
        var defaults = new JsonObject();
        foreach (var (fieldKey, (type, defaultValue)) in CalculationScopeBuilder.DescribeInputs(definition))
        {
            inputTypes[fieldKey] = type;
            if (defaultValue is not null)
            {
                defaults[fieldKey] = defaultValue;
            }
        }

        var serviceValues = new JsonObject();
        foreach (var (name, field) in calc.Set.Fields)
        {
            if (string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase)
                && calc.Scope.TryGetValue(name, out var value))
            {
                serviceValues[name] = ScopeValueToJson(value);
            }
        }

        return new JsonObject
        {
            ["calculations"] = JsonSerializer.SerializeToNode(calc.Set, LiveModelJsonOptions),
            ["inputTypes"] = inputTypes,
            ["defaults"] = defaults,
            ["service"] = serviceValues
        };
    }

    public static JsonNode? ScopeValueToJson(object? value) => value switch
    {
        null => null,
        decimal d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        // Any other plain CLR numeric type a host's own serviceInputsResolver might reasonably
        // return (e.g. a plain `int Age` on its own member-record type, not pre-cast to
        // decimal) — found live: without these, an int fell through to the string.ToString()
        // case below and silently produced a JSON *string* ("47") instead of a JSON number
        // (47) in the embedded [data-wayfinder-live-model] payload. The client's own
        // toScope/calculation engine (UmbracoPrism.Client) only type-converts genuine JSON
        // numbers into evaluator Dec values, so every expression referencing the field (e.g.
        // "max(55, member.age + 1)") threw "Expected a number but got '47'" — which silently
        // aborted the client-side live-form's entire re-evaluation, leaving whatever the
        // server happened to render (a chart's bars/legend included) stuck uncorrected.
        int i => JsonValue.Create((decimal)i),
        long l => JsonValue.Create((decimal)l),
        short s => JsonValue.Create((decimal)s),
        byte by => JsonValue.Create((decimal)by),
        double db => JsonValue.Create((decimal)db),
        float f => JsonValue.Create((decimal)f),
        string text => JsonValue.Create(text),
        IReadOnlyDictionary<string, object?> map => new JsonObject(
            map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ScopeValueToJson(pair.Value)))),
        _ => JsonValue.Create(value.ToString())
    };

    /// <summary>
    /// Shared by <see cref="Components.Component.ShowWhen"/> (component visibility) and
    /// <see cref="ServiceBlueprintRouteDefinition.ShowWhen"/> (route/action availability) — takes
    /// a raw scope rather than a <see cref="CalculationRenderContext"/> so a caller with no other
    /// use for the fuller context (route gating doesn't need <c>Result</c>/<c>Display</c>) isn't
    /// forced to build one just to call this.
    /// </summary>
    public bool EvaluateShowWhen(
        string? showWhen,
        IReadOnlyDictionary<string, object?>? scope,
        ServiceBlueprintCalculationSet? calculations)
    {
        if (string.IsNullOrWhiteSpace(showWhen) || scope is null)
        {
            return true;
        }

        try
        {
            return _calculationEvaluator.EvaluateExpression(showWhen, scope, calculations) is not false;
        }
        catch (CalculationException exception)
        {
            ShowWhenFailed(logger, exception, showWhen);
            return true;
        }
    }

    /// <summary>
    /// The scope a <c>showWhen</c>/stage-validation expression evaluates against: declared inputs
    /// plus calculated fields, exactly what <see cref="EvaluateDefinitionCalculations"/> also
    /// builds — but without that method's side effect of persisting
    /// <see cref="ServiceRequest.LastCalculationResult"/>, so it's safe to call from a hot,
    /// no-writes path like <see cref="BuildAvailableActions"/> (once per stage per queue render)
    /// without multiplying instance-store writes.
    /// </summary>
    /// <param name="pendingFieldValues">
    /// A submission in progress, merged over <paramref name="instance"/>'s already-persisted
    /// values before evaluating — <see langword="null"/> to evaluate against persisted state
    /// alone (what a caller deciding what to *render*, rather than validating a submission,
    /// wants).
    /// </param>
    public Dictionary<string, object?> BuildCalculationScope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage,
        Dictionary<string, object?>? pendingFieldValues)
    {
        var serviceInputs = resolveServiceInputs(instance, definition, stage);
        var mergedFieldValues = pendingFieldValues is null
            ? instance.FieldValues
            : Merge(instance.FieldValues, pendingFieldValues);
        var baseScope = CalculationScopeBuilder.Build(definition, mergedFieldValues, serviceInputs);

        if (definition.Calculations is null)
        {
            return baseScope;
        }

        var evaluation = _calculationEvaluator.EvaluateCollectingErrors(definition.Calculations, baseScope);
        var fullScope = new Dictionary<string, object?>(baseScope, StringComparer.Ordinal);
        foreach (var (name, value) in evaluation.Result.Fields)
        {
            fullScope[name] = value;
        }
        return fullScope;
    }

    /// <summary>
    /// Evaluates <paramref name="stage"/>'s declarative <see cref="StageDefinition.Validations"/>
    /// against the merge of persisted + just-submitted field values — the same trust boundary
    /// <see cref="Advance(string, string, string, ActorProfile, string, int, Dictionary{string, object?})"/>
    /// already applies to field-level validation: never the stale persisted instance alone, never
    /// anything the client could claim was pre-validated.
    ///
    /// Failure is deliberately biased toward blocking, not toward permissiveness, unlike
    /// <see cref="EvaluateShowWhen"/>'s "stays visible" default: a <c>when</c> that doesn't
    /// evaluate to exactly <c>false</c> is treated as applying (ambiguous → check it), a
    /// <c>rule</c> that doesn't evaluate to exactly <c>true</c> is treated as failed (ambiguous →
    /// block), and a rule whose expressions throw is treated as failed rather than skipped — this
    /// is a hard gate, not a display hint, so an expression this engine can't confirm holds must
    /// never silently let a submission through. This should be rare in practice:
    /// <c>ServiceBlueprintAuthoringService.Validate</c> already statically checks every
    /// <c>when</c>/<c>rule</c> expression before a blueprint can be saved. A calculated field that
    /// fails only affects the specific rules that actually reference it (via
    /// <see cref="CalculationEvaluator.EvaluateCollectingErrors"/>), not every validation on the
    /// stage.
    /// </summary>
    public List<ServiceRequestProblem> EvaluateStageValidations(
        ServiceRequest instance,
        ServiceBlueprint definition,
        StageDefinition stage,
        Dictionary<string, object?>? fieldValues,
        string action)
    {
        // A rule naming no actions guards every way out of the stage (the default, and what a
        // data-completeness rule wants). A rule naming actions guards only those — see
        // ServiceBlueprintStageValidationRule.Actions for why a stage with genuinely different
        // exits needs this to be expressible at all.
        var rules = (stage.Validations ?? [])
            .Where(rule => rule.Actions is not { Count: > 0 } scoped
                || scoped.Contains(action, StringComparer.Ordinal))
            .ToArray();

        if (rules.Length == 0)
        {
            return [];
        }

        var scope = BuildCalculationScope(instance, definition, stage, fieldValues);

        var problems = new List<ServiceRequestProblem>();
        foreach (var rule in rules)
        {
            bool failed;
            try
            {
                var applies = string.IsNullOrWhiteSpace(rule.When)
                    || _calculationEvaluator.EvaluateExpression(rule.When, scope, definition.Calculations) is not false;
                failed = applies
                    && _calculationEvaluator.EvaluateExpression(rule.Rule, scope, definition.Calculations) is not true;
            }
            catch (CalculationException exception)
            {
                RuleFailedToEvaluate(logger, exception, rule.Code, definition.DefinitionKey, stage.StageKey);
                failed = true;
            }

            if (failed)
            {
                problems.Add(new ServiceRequestProblem
                {
                    FieldKey = rule.Field ?? stage.StageKey,
                    Message = rule.Message,
                    Code = rule.Code
                });
            }
        }

        return problems;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Calculation evaluation failed for blueprint {Key}, stage {State}; rendering without calculated values.")]
    private static partial void CalculationFailed(ILogger logger, Exception exception, string key, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "showWhen expression '{Expr}' failed; stays visible.")]
    private static partial void ShowWhenFailed(ILogger logger, Exception exception, string expr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stage validation rule '{Code}' failed to evaluate for blueprint {Key}, stage {State}; treating as failed.")]
    private static partial void RuleFailedToEvaluate(ILogger logger, Exception exception, string code, string key, string state);
}
