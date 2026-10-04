using Wayfinder.Engine.Abstractions;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// Validates a blueprint without running it: gateway routing, every stat-group/chart binding against
/// the fields and series that exist, every field's conditionalOn/defaultFrom, reachability, request
/// policy, support-system and bulk-dataset actions, component properties, the host's queue
/// capabilities, the <c>calculations</c> block, and every <c>showWhen</c>/stage-validation
/// expression — collecting every diagnostic rather than stopping at the first.
/// A declared <c>source: "service"</c> field not covered by the mock inputs can't be verified
/// statically: it is reported as a Warning, not an error, and the expressions that depend on it are
/// reported as unverified rather than failed (see <see cref="StaticScopeGaps"/>).
/// </summary>
internal sealed class ServiceBlueprintValidator(
    IReadOnlyList<IServiceBlueprintStructuralValidator> structuralValidators,
    IQueueCapabilitiesProvider? queueCapabilities)
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyFieldValues = new Dictionary<string, object?>();

    private static readonly EvaluationCodes FieldCodes = new("CALC_FIELD_ERROR", "CALC_FIELD_UNVERIFIED");
    private static readonly EvaluationCodes SeriesCodes = new("CALC_SERIES_ERROR", "CALC_SERIES_UNVERIFIED");

    private readonly QueueCapabilityChecks _queueChecks = new(queueCapabilities);

    public ServiceBlueprintValidationOutcome Validate(
        ServiceBlueprint blueprint,
        IReadOnlyDictionary<string, object?>? mockServiceInputs = null)
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>(StructuralDiagnostics(blueprint));

        var serviceInputs = StaticServiceInputs.Build(blueprint.Calculations, mockServiceInputs);
        var gaps = StaticScopeGaps.For(blueprint, serviceInputs);
        diagnostics.AddRange(serviceInputs.UnverifiedWarnings());

        var evaluator = new CalculationEvaluator();
        // With no calculations block there is nothing to resolve, so the caller's own mock values stand in as given.
        var serviceValues = blueprint.Calculations is null ? mockServiceInputs : serviceInputs.Resolved;
        var (expressionScope, calculationDiagnostics) = EvaluateCalculations(blueprint, evaluator, serviceValues, gaps);
        diagnostics.AddRange(calculationDiagnostics);
        diagnostics.AddRange(new StaticExpressionChecks(evaluator, blueprint, expressionScope, gaps).Diagnostics());

        return new ServiceBlueprintValidationOutcome(
            !diagnostics.Any(diagnostic => diagnostic.Severity == ServiceBlueprintDiagnosticSeverity.Error), diagnostics);
    }

    private IEnumerable<ServiceBlueprintDiagnostic> StructuralDiagnostics(ServiceBlueprint blueprint) =>
        blueprint.ValidateGatewayRouting()
            .Concat(blueprint.ValidateDataDisplayBindings())
            .Concat(blueprint.ValidateFieldReferences())
            .Concat(blueprint.ValidateReachability())
            .Concat(blueprint.ValidateRequestPolicy())
            .Concat(blueprint.ValidateSupportSystemActions())
            .Concat(blueprint.ValidateBulkDatasetActions())
            .Concat(ComponentPropertyDiagnostics(blueprint))
            .Concat(_queueChecks.Diagnostics(blueprint))
            .Concat(structuralValidators.SelectMany(validator => validator.Validate(blueprint)));

    /// <summary>
    /// Evaluates the <c>calculations</c> block, returning a diagnostic for each field/series that
    /// fails, and the scope later expressions are checked against: the inputs plus every calculated
    /// field that did resolve.
    /// </summary>
    private static (Dictionary<string, object?> Scope, IReadOnlyList<ServiceBlueprintDiagnostic> Diagnostics) EvaluateCalculations(
        ServiceBlueprint blueprint,
        CalculationEvaluator evaluator,
        IReadOnlyDictionary<string, object?>? serviceValues,
        StaticScopeGaps gaps)
    {
        var scope = CalculationScopeBuilder.Build(blueprint, EmptyFieldValues, serviceValues);
        if (blueprint.Calculations is null)
        {
            return (scope, []);
        }

        var evaluation = evaluator.EvaluateCollectingErrors(blueprint.Calculations, scope);
        var merged = new Dictionary<string, object?>(scope, StringComparer.Ordinal);
        foreach (var (name, value) in evaluation.Result.Fields)
        {
            merged[name] = value;
        }

        return (merged, evaluation.Diagnostics.Select(failure => Classify(failure, gaps)).ToList());
    }

    private static ServiceBlueprintDiagnostic Classify(CalculationDiagnostic failure, StaticScopeGaps gaps) =>
        failure.Kind == CalculationDiagnosticKind.Field
            ? gaps.Classify(FieldCodes, $"calculations.fields.{failure.Name}", failure.Message, failure.Name)
            : gaps.Classify(SeriesCodes, $"calculations.series.{failure.Name}", failure.Message, failure.Name);

    /// <summary>
    /// Validates every component against its own registered <see cref="ComponentDescriptor"/> —
    /// required properties, allowed values, patterns, length/numeric constraints, and that every
    /// conditional-child key matches a declared option. See <see cref="ComponentPropertyValidator"/>.
    /// </summary>
    private static IEnumerable<ServiceBlueprintDiagnostic> ComponentPropertyDiagnostics(ServiceBlueprint blueprint) =>
        blueprint.Stages
            .SelectMany(stage => stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            .SelectMany(entry => ComponentPropertyValidator.Validate(entry.Component, ComponentTypeRegistry.DescriptorFor(entry.Component), entry.Path));
}
