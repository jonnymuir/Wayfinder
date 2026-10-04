using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// Statically evaluates every authored expression that is not part of <c>calculations</c> itself —
/// component and route <c>showWhen</c>, stage validation <c>when</c>/<c>rule</c> — against
/// <paramref name="scope"/>, and flags the <c>showWhen</c> on a gateway's own routes that can never
/// take effect.
/// </summary>
internal sealed class StaticExpressionChecks(
    CalculationEvaluator evaluator,
    ServiceBlueprint blueprint,
    IReadOnlyDictionary<string, object?> scope,
    StaticScopeGaps gaps)
{
    private static readonly EvaluationCodes ComponentShowWhen = new("SHOW_WHEN_EVAL_ERROR", "SHOW_WHEN_UNVERIFIED");
    private static readonly EvaluationCodes RouteShowWhen = new("ROUTE_SHOW_WHEN_EVAL_ERROR", "ROUTE_SHOW_WHEN_UNVERIFIED");
    private static readonly EvaluationCodes StageValidationWhen = new("STAGE_VALIDATION_WHEN_EVAL_ERROR", "STAGE_VALIDATION_WHEN_UNVERIFIED");
    private static readonly EvaluationCodes StageValidationRule = new("STAGE_VALIDATION_RULE_EVAL_ERROR", "STAGE_VALIDATION_RULE_UNVERIFIED");

    public IEnumerable<ServiceBlueprintDiagnostic> Diagnostics()
    {
        foreach (var stage in blueprint.Stages)
        {
            foreach (var diagnostic in ComponentDiagnostics(stage).Concat(StageValidationDiagnostics(stage)).Concat(StageRouteDiagnostics(stage)))
            {
                yield return diagnostic;
            }
        }

        foreach (var diagnostic in GatewayRouteDiagnostics())
        {
            yield return diagnostic;
        }
    }

    private IEnumerable<ServiceBlueprintDiagnostic> ComponentDiagnostics(StageDefinition stage) =>
        stage.Components
            .FlattenWithPaths($"stages.{stage.StageKey}.components")
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Component.ShowWhen))
            .SelectMany(entry => Check(entry.Component.ShowWhen!, ComponentShowWhen, $"{entry.Path}.showWhen"));

    /// <summary>
    /// Unlike <c>showWhen</c> (a display hint, tolerant of any non-<c>false</c> result), a stage
    /// validation <c>when</c>/<c>rule</c> must evaluate to a real boolean: <c>ProcessManagerEngine</c>
    /// would silently treat a number or string as "not exactly true" and fail the rule on every
    /// submission, a much harder bug to spot than a diagnostic caught at save time.
    /// </summary>
    private IEnumerable<ServiceBlueprintDiagnostic> StageValidationDiagnostics(StageDefinition stage)
    {
        var index = 0;
        foreach (var rule in stage.Validations ?? [])
        {
            var path = $"stages.{stage.StageKey}.validations[{index++}]";
            if (!string.IsNullOrWhiteSpace(rule.When))
            {
                foreach (var diagnostic in Check(rule.When, StageValidationWhen, $"{path}.when", requireBoolean: true))
                {
                    yield return diagnostic;
                }
            }

            foreach (var diagnostic in Check(rule.Rule, StageValidationRule, $"{path}.rule", requireBoolean: true))
            {
                yield return diagnostic;
            }
        }
    }

    /// <summary>
    /// A stage route's <c>ShowWhen</c> is evaluated by ProcessManagerEngine with the same tolerant,
    /// fail-open bias a component's has, so it is checked the same way: a parse/reference error is
    /// flagged, but a clean non-boolean result is not.
    /// </summary>
    private IEnumerable<ServiceBlueprintDiagnostic> StageRouteDiagnostics(StageDefinition stage) =>
        (stage.Routes ?? [])
            .Select((route, index) => (route, path: $"stages.{stage.StageKey}.routes[{index}]"))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.route.ShowWhen))
            .SelectMany(entry => Check(entry.route.ShowWhen!, RouteShowWhen, $"{entry.path}.showWhen"));

    /// <summary>
    /// A gateway's own routes never go through ProcessManagerEngine.BuildAvailableActions — a Split
    /// gateway fans out to every outgoing route regardless, and a Join gateway selects its one
    /// outgoing route by matching the arriving trigger. A <c>ShowWhen</c> set there would silently do
    /// nothing, so it is flagged rather than left to be found the hard way.
    /// </summary>
    private IEnumerable<ServiceBlueprintDiagnostic> GatewayRouteDiagnostics() =>
        (blueprint.Gateways ?? []).SelectMany(gateway =>
            (gateway.Routes ?? [])
                .Select((route, index) => (route, path: $"gateways.{gateway.Key}.routes[{index}]"))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.route.ShowWhen))
                .Select(entry => new ServiceBlueprintDiagnostic(
                    "ROUTE_SHOW_WHEN_ON_GATEWAY_ROUTE",
                    $"{entry.path}.showWhen",
                    "showWhen has no effect on a gateway's own routes — a Split gateway always follows " +
                    "every outgoing route regardless, and a Join gateway selects by matching the arriving " +
                    "trigger, not by this expression. Move this route's condition onto the stage that " +
                    "owns it instead.",
                    ServiceBlueprintDiagnosticSeverity.Warning)));

    /// <summary>Evaluates <paramref name="expression"/>; yields a diagnostic only if it cannot be evaluated (or, when asked, is not a boolean).</summary>
    private IEnumerable<ServiceBlueprintDiagnostic> Check(string expression, EvaluationCodes codes, string path, bool requireBoolean = false)
    {
        object? result;
        try
        {
            result = evaluator.EvaluateExpression(expression, scope, blueprint.Calculations);
        }
        catch (CalculationException ex)
        {
            return [gaps.Classify(codes, path, ex.Message)];
        }

        return requireBoolean && result is not bool
            ? [new ServiceBlueprintDiagnostic(
                codes.Error,
                path,
                $"Expression '{expression}' evaluates to {(result is null ? "nothing" : $"'{result}'")}, " +
                "not true/false. Stage validations are boolean gates — fix the expression so it always " +
                "resolves to a real boolean.")]
            : [];
    }
}
