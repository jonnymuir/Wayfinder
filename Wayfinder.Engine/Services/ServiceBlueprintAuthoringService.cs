using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Services;

/// <summary>Outcome of validating a service blueprint.</summary>
public sealed record ServiceBlueprintValidationOutcome(bool IsValid, IReadOnlyList<ServiceBlueprintDiagnostic> Diagnostics)
{
    public static ServiceBlueprintValidationOutcome Valid { get; } = new(true, Array.Empty<ServiceBlueprintDiagnostic>());
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceBlueprintSaveStatus>))]
public enum ServiceBlueprintSaveStatus { Saved, Invalid, Conflict }

/// <summary>
/// Outcome of a <see cref="ServiceBlueprintAuthoringService.SaveAsync"/> call — distinguishes a
/// successful save from a validation failure (<see cref="Diagnostics"/> from <see cref="ServiceBlueprintAuthoringService.Validate"/>)
/// and from an optimistic-concurrency conflict (<see cref="CurrentVersion"/> is what's actually
/// persisted now; the caller's <c>expectedVersion</c> was stale).
/// </summary>
public sealed record ServiceBlueprintSaveOutcome(
    ServiceBlueprintSaveStatus Status,
    IReadOnlyList<ServiceBlueprintDiagnostic> Diagnostics,
    int? CurrentVersion = null,
    int? NewVersion = null)
{
    public bool IsSaved => Status == ServiceBlueprintSaveStatus.Saved;

    public static ServiceBlueprintSaveOutcome Saved(int newVersion) =>
        new(ServiceBlueprintSaveStatus.Saved, Array.Empty<ServiceBlueprintDiagnostic>(), NewVersion: newVersion);

    public static ServiceBlueprintSaveOutcome Invalid(IReadOnlyList<ServiceBlueprintDiagnostic> diagnostics) =>
        new(ServiceBlueprintSaveStatus.Invalid, diagnostics);

    public static ServiceBlueprintSaveOutcome Conflict(int currentVersion) =>
        new(
            ServiceBlueprintSaveStatus.Conflict,
            [new ServiceBlueprintDiagnostic(
                "SAVE_VERSION_CONFLICT",
                "version",
                $"Blueprint has changed since it was loaded — current version is {currentVersion}, which didn't match the expected version. Reload and reapply your change.")],
            CurrentVersion: currentVersion);
}

/// <summary>
/// Transport-agnostic service blueprint authoring surface: list/read/validate/save/simulate
/// definitions against a host-supplied <see cref="IServiceBlueprintSourceStore"/>. Reusable by
/// any front door (MCP tools, a CLI, a host's own code) — no MCP dependency here.
/// </summary>
public sealed class ServiceBlueprintAuthoringService(
    IServiceBlueprintSourceStore store,
    IEnumerable<IServiceBlueprintStructuralValidator>? structuralValidators = null,
    IQueueCapabilitiesProvider? queueCapabilities = null)
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyFieldValues =
        new Dictionary<string, object?>();

    private readonly IReadOnlyList<IServiceBlueprintStructuralValidator> _structuralValidators =
        structuralValidators?.ToArray() ?? [];

    public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
        store.ListAsync(ct);

    public Task<ServiceBlueprint?> ReadAsync(string definitionKey, CancellationToken ct = default) =>
        store.LoadAsync(definitionKey, ct);

    public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
        store.DeleteAsync(definitionKey, ct);

    /// <summary>
    /// Every queue this host has declared render capabilities for, per
    /// <see cref="IQueueCapabilitiesProvider.GetAllDeclaredCapabilities"/> — empty if no
    /// provider is registered.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetQueueCapabilities() =>
        queueCapabilities?.GetAllDeclaredCapabilities() ?? new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Validates gateway routing, every stat-group/chart binding against the fields and series
    /// that actually exist, every field's conditionalOn/defaultFrom against the calculation
    /// scope and its own stage's other fields, the <c>calculations</c> block, and every
    /// component's <c>showWhen</c> expression, collecting every diagnostic rather than stopping
    /// at the first. A declared <c>source: "service"</c> field not covered by
    /// <paramref name="mockServiceInputs"/> can't be verified statically — it's reported as a
    /// <see cref="ServiceBlueprintDiagnosticSeverity.Warning"/>, not an error, and (since the calculated
    /// fields and any showWhen depending on them can't be evaluated without it) the rest of the
    /// calculations/showWhen checks are skipped for that call. Pass real values via
    /// <paramref name="mockServiceInputs"/>, or use <c>Simulate</c>, to verify those fully.
    /// </summary>
    public ServiceBlueprintValidationOutcome Validate(
        ServiceBlueprint blueprint,
        IReadOnlyDictionary<string, object?>? mockServiceInputs = null)
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>(blueprint.ValidateGatewayRouting());
        diagnostics.AddRange(blueprint.ValidateDataDisplayBindings());
        diagnostics.AddRange(blueprint.ValidateFieldReferences());
        diagnostics.AddRange(blueprint.ValidateReachability());
        diagnostics.AddRange(blueprint.ValidateStageVocabulary());
        diagnostics.AddRange(blueprint.ValidateSupportSystemActions());
        diagnostics.AddRange(blueprint.ValidateBulkDatasetActions());
        diagnostics.AddRange(ValidateComponentProperties(blueprint));
        diagnostics.AddRange(ValidateQueueCapabilityDeclarations());
        diagnostics.AddRange(ValidateQueueCapabilities(blueprint));
        foreach (var validator in _structuralValidators)
        {
            diagnostics.AddRange(validator.Validate(blueprint));
        }
        var evaluator = new CalculationEvaluator();

        // Only numeric fields can still be genuinely unresolvable here — CalculationScopeBuilder.Build
        // now gives every string/boolean field a safe "nothing here" placeholder ("" / false) even
        // with neither a real submission nor a declared default, since design-time validation never
        // has a real citizen behind it to supply one. A missing number has no equally safe
        // placeholder (0 is a real, meaningful value), so it still requires an explicit default —
        // computed once, up front, since calc fields/showWhen/stage validations below all need it.
        var numericInputsWithoutDefault = CalculationScopeBuilder.DescribeInputs(blueprint)
            .Where(kvp => kvp.Value.Type == "number" && string.IsNullOrWhiteSpace(kvp.Value.Default))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyDictionary<string, object?> showWhenScope;

        if (blueprint.Calculations is null)
        {
            showWhenScope = CalculationScopeBuilder.Build(blueprint, EmptyFieldValues, mockServiceInputs);
        }
        else
        {
            var unresolvedServiceFields = blueprint.Calculations.Fields
                .Where(f => string.Equals(f.Value.Source, "service", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .Where(name => mockServiceInputs is null || !mockServiceInputs.ContainsKey(name))
                .ToList();

            if (unresolvedServiceFields.Count > 0)
            {
                foreach (var name in unresolvedServiceFields)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "CALC_SERVICE_FIELD_UNVERIFIED",
                        $"calculations.fields.{name}",
                        $"Field '{name}' is service-sourced; validate cannot supply a real value for it " +
                        "statically. Pass mockServiceInputs, or use simulate_service_blueprint, to verify " +
                        "calculations that depend on it.",
                        ServiceBlueprintDiagnosticSeverity.Warning));
                }

                // Calculated fields, and any showWhen depending on them, can't be reliably
                // evaluated without every service field resolved — stop here for this call.
                return new ServiceBlueprintValidationOutcome(
                    !diagnostics.Any(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error), diagnostics);
            }

            var scope = CalculationScopeBuilder.Build(blueprint, EmptyFieldValues, mockServiceInputs);
            var evaluation = evaluator.EvaluateCollectingErrors(blueprint.Calculations, scope);
            foreach (var fieldOrSeries in evaluation.Diagnostics)
            {
                var (errorCode, unverifiedCode, path) = fieldOrSeries.Kind == CalculationDiagnosticKind.Field
                    ? ("CALC_FIELD_ERROR", "CALC_FIELD_UNVERIFIED", $"calculations.fields.{fieldOrSeries.Name}")
                    : ("CALC_SERIES_ERROR", "CALC_SERIES_UNVERIFIED", $"calculations.series.{fieldOrSeries.Name}");
                diagnostics.Add(ClassifyEvalDiagnostic(
                    errorCode, unverifiedCode, path, fieldOrSeries.Message, numericInputsWithoutDefault));
            }

            var mergedScope = new Dictionary<string, object?>(scope, StringComparer.Ordinal);
            foreach (var (name, value) in evaluation.Result.Fields)
            {
                mergedScope[name] = value;
            }

            showWhenScope = mergedScope;
        }

        foreach (var stage in blueprint.Stages)
        {
            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                if (string.IsNullOrWhiteSpace(component.ShowWhen))
                {
                    continue;
                }

                try
                {
                    evaluator.EvaluateExpression(component.ShowWhen, showWhenScope, blueprint.Calculations);
                }
                catch (CalculationException ex)
                {
                    diagnostics.Add(ClassifyEvalDiagnostic(
                        "SHOW_WHEN_EVAL_ERROR", "SHOW_WHEN_UNVERIFIED", $"{path}.showWhen", ex.Message,
                        numericInputsWithoutDefault));
                }
            }

            var validationIndex = 0;
            foreach (var rule in stage.Validations ?? [])
            {
                var path = $"stages.{stage.StageKey}.validations[{validationIndex}]";
                validationIndex++;

                if (!string.IsNullOrWhiteSpace(rule.When))
                {
                    CheckStageValidationExpression(
                        evaluator, rule.When, showWhenScope, blueprint.Calculations,
                        "STAGE_VALIDATION_WHEN_EVAL_ERROR", "STAGE_VALIDATION_WHEN_UNVERIFIED", $"{path}.when",
                        numericInputsWithoutDefault, diagnostics);
                }

                CheckStageValidationExpression(
                    evaluator, rule.Rule, showWhenScope, blueprint.Calculations,
                    "STAGE_VALIDATION_RULE_EVAL_ERROR", "STAGE_VALIDATION_RULE_UNVERIFIED", $"{path}.rule",
                    numericInputsWithoutDefault, diagnostics);
            }

            // ServiceBlueprintRouteDefinition.ShowWhen is evaluated by ProcessManagerEngine with
            // exactly the tolerant, fail-open bias component.ShowWhen has above (any non-false
            // result stays visible) — checked the same way here: a parse/reference error is
            // flagged, but (unlike a stage validation's when/rule) a clean non-boolean result is
            // not, since that's the real runtime behaviour a route's ShowWhen is documented to have.
            var routeIndex = 0;
            foreach (var route in stage.Routes ?? [])
            {
                var routePath = $"stages.{stage.StageKey}.routes[{routeIndex}]";
                routeIndex++;

                if (string.IsNullOrWhiteSpace(route.ShowWhen))
                {
                    continue;
                }

                try
                {
                    evaluator.EvaluateExpression(route.ShowWhen, showWhenScope, blueprint.Calculations);
                }
                catch (CalculationException ex)
                {
                    diagnostics.Add(ClassifyEvalDiagnostic(
                        "ROUTE_SHOW_WHEN_EVAL_ERROR", "ROUTE_SHOW_WHEN_UNVERIFIED", $"{routePath}.showWhen",
                        ex.Message, numericInputsWithoutDefault));
                }
            }
        }

        // A gateway's own routes never go through ProcessManagerEngine.BuildAvailableActions —
        // a Split gateway fans out to every outgoing route regardless (that's what makes the
        // multi-cursor Join model work at all), and a Join gateway selects its one outgoing route
        // by matching the arriving trigger, not by evaluating anything. ShowWhen set there would
        // silently do nothing rather than the author's intended thing, so it's flagged rather than
        // left to be found the hard way — the same reasoning that made replacing the old, equally
        // silent always/event/guard route-condition UI worth doing.
        foreach (var gateway in blueprint.Gateways ?? [])
        {
            var routeIndex = 0;
            foreach (var route in gateway.Routes ?? [])
            {
                var routePath = $"gateways.{gateway.Key}.routes[{routeIndex}]";
                routeIndex++;

                if (!string.IsNullOrWhiteSpace(route.ShowWhen))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "ROUTE_SHOW_WHEN_ON_GATEWAY_ROUTE",
                        $"{routePath}.showWhen",
                        "showWhen has no effect on a gateway's own routes — a Split gateway always follows " +
                        "every outgoing route regardless, and a Join gateway selects by matching the arriving " +
                        "trigger, not by this expression. Move this route's condition onto the stage that " +
                        "owns it instead.",
                        ServiceBlueprintDiagnosticSeverity.Warning));
                }
            }
        }

        return new ServiceBlueprintValidationOutcome(
            !diagnostics.Any(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error), diagnostics);
    }

    /// <summary>
    /// Statically evaluates one <c>StageDefinition.Validations</c> <c>when</c>/<c>rule</c>
    /// expression against the same scope <c>showWhen</c> is checked with, reporting a parse/type/
    /// reference error (<c>CalculationException</c>) exactly like <c>ValidateShowWhen</c> does.
    /// Additionally requires the result to be a real boolean — unlike <c>showWhen</c> (a display
    /// hint, tolerant of any non-<c>false</c> result), a rule that evaluates cleanly to a number
    /// or string is still an authoring mistake: <c>ProcessManagerEngine</c> would silently treat
    /// it as "not exactly true" and fail the rule on every submission, a much harder bug to spot
    /// than a diagnostic caught here at save time.
    /// </summary>
    private static void CheckStageValidationExpression(
        CalculationEvaluator evaluator,
        string expression,
        IReadOnlyDictionary<string, object?> scope,
        ServiceBlueprintCalculationSet? calculations,
        string errorCode,
        string unverifiedCode,
        string path,
        IReadOnlySet<string> numericInputsWithoutDefault,
        List<ServiceBlueprintDiagnostic> diagnostics)
    {
        try
        {
            var result = evaluator.EvaluateExpression(expression, scope, calculations);
            if (result is not bool)
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    errorCode,
                    path,
                    $"Expression '{expression}' evaluates to {(result is null ? "nothing" : $"'{result}'")}, " +
                    "not true/false. Stage validations are boolean gates — fix the expression so it always " +
                    "resolves to a real boolean."));
            }
        }
        catch (CalculationException ex)
        {
            diagnostics.Add(ClassifyEvalDiagnostic(errorCode, unverifiedCode, path, ex.Message, numericInputsWithoutDefault));
        }
    }

    /// <summary>
    /// Validates every component in the blueprint against its own registered
    /// <see cref="ComponentDescriptor"/> — required properties, allowed values, patterns,
    /// length/numeric constraints, and (for a <c>ConditionalChildren</c>-style container)
    /// that every conditional-child key actually matches a declared option. See
    /// <see cref="ComponentPropertyValidator"/>.
    /// </summary>
    private static IEnumerable<ServiceBlueprintDiagnostic> ValidateComponentProperties(ServiceBlueprint blueprint)
    {
        foreach (var stage in blueprint.Stages)
        {
            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                var descriptor = ComponentTypeRegistry.DescriptorFor(component);
                foreach (var diagnostic in ComponentPropertyValidator.Validate(component, descriptor, path))
                {
                    yield return diagnostic;
                }
            }
        }
    }

    /// <summary>
    /// Cross-checks every discriminator string a registered <see cref="IQueueCapabilitiesProvider"/>
    /// declares against <see cref="ComponentTypeRegistry"/> itself — catching a typo'd capability
    /// string (e.g. <c>"texts"</c> instead of <c>"text"</c>) directly at its source, rather than
    /// only as a downstream symptom (every component of the intended type silently reported as
    /// unsupported by <see cref="ValidateQueueCapabilities"/>). Runs unconditionally, independent
    /// of what <paramref name="blueprint"/> actually contains — a host's declared capabilities are
    /// static configuration, not blueprint content, so a typo in a queue nothing currently
    /// authors for would otherwise go unnoticed indefinitely.
    /// </summary>
    private IEnumerable<ServiceBlueprintDiagnostic> ValidateQueueCapabilityDeclarations()
    {
        if (queueCapabilities is null)
        {
            yield break;
        }

        foreach (var (queueKey, supportedTypes) in queueCapabilities.GetAllDeclaredCapabilities())
        {
            foreach (var discriminator in supportedTypes)
            {
                if (ComponentTypeRegistry.Find(discriminator) is not null)
                {
                    continue;
                }

                yield return new ServiceBlueprintDiagnostic(
                    "QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE",
                    $"queues.{queueKey}",
                    $"Queue '{queueKey}' declares support for component type '{discriminator}', but no such " +
                    "type is registered in ComponentTypeRegistry — check for a typo. Call list_component_types " +
                    "to see every valid discriminator.");
            }
        }
    }

    /// <summary>
    /// When a host registers <see cref="IQueueCapabilitiesProvider"/>, reject any stage whose
    /// components exceed what its queue's host can actually render — otherwise a blueprint can
    /// be authored/saved with a component that silently renders as nothing at runtime. A queue
    /// with no declaration at all is unrestricted (not this host's concern).
    /// </summary>
    private IEnumerable<ServiceBlueprintDiagnostic> ValidateQueueCapabilities(ServiceBlueprint blueprint)
    {
        if (queueCapabilities is null)
        {
            yield break;
        }

        foreach (var stage in blueprint.Stages)
        {
            var supportedTypes = queueCapabilities.GetSupportedComponentTypes(stage.QueueKey);
            if (supportedTypes is null)
            {
                continue;
            }

            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                var discriminator = ComponentTypeRegistry.DiscriminatorFor(component);
                if (supportedTypes.Contains(discriminator, StringComparer.Ordinal))
                {
                    continue;
                }

                yield return new ServiceBlueprintDiagnostic(
                    "QUEUE_CAPABILITY_UNSUPPORTED_COMPONENT",
                    path,
                    $"State '{stage.StageKey}' uses component type '{discriminator}', which queue " +
                    $"'{stage.QueueKey}''s host does not declare support for " +
                    (supportedTypes.Count == 0
                        ? "(it currently supports no component types at all). "
                        : $"(it supports: {string.Join(", ", supportedTypes)}). ") +
                    "Remove/replace this component, or extend that host's IQueueCapabilitiesProvider " +
                    "declaration once it can actually render it. Call list_queue_capabilities to check " +
                    "what a queue supports before drafting for it.");
            }
        }
    }

    private static readonly Regex UnknownNamePattern = new(@"^Unknown name '([^']+)' in", RegexOptions.Compiled);

    /// <summary>
    /// Validation has no real submitted data, but <see cref="CalculationScopeBuilder.Build"/> now
    /// gives every string/boolean input a safe placeholder ("" / false) regardless — a missing
    /// default no longer makes those fields "Unknown" here. A numeric field is the one case left
    /// where that's not possible (0 is a real, meaningful value, not a safe stand-in for "nothing
    /// submitted yet"), so referencing one with no declared default is expected to fail static
    /// evaluation; that's not an authoring mistake, just a limit of what validation (as opposed to
    /// simulate_service_blueprint, which takes real field values) can check. Downgrade exactly that
    /// case to a Warning with an explanation, matching the CALC_SERVICE_FIELD_UNVERIFIED precedent;
    /// anything else genuinely is an error.
    /// </summary>
    private static ServiceBlueprintDiagnostic ClassifyEvalDiagnostic(
        string errorCode,
        string unverifiedCode,
        string path,
        string message,
        IReadOnlySet<string> numericInputsWithoutDefault)
    {
        var match = UnknownNamePattern.Match(message);
        if (!match.Success || !numericInputsWithoutDefault.Contains(match.Groups[1].Value))
        {
            return new ServiceBlueprintDiagnostic(errorCode, path, message);
        }

        var fieldKey = match.Groups[1].Value;
        return new ServiceBlueprintDiagnostic(
            unverifiedCode,
            path,
            $"{message} '{fieldKey}' is a real numeric input field on this blueprint, but it has no " +
            "declared \"default\" value and there's no real submitted data to fall back on outside a live " +
            "instance — unlike text/checkbox fields, there's no safe placeholder for a missing number, so " +
            "validate_service_blueprint can't verify this expression statically. Add a default to that " +
            "component to verify it here, or use simulate_service_blueprint with real field values instead.",
            ServiceBlueprintDiagnosticSeverity.Warning);
    }

    /// <summary>
    /// Validates, then saves only if <paramref name="expectedVersion"/> still matches what's
    /// currently persisted (see <see cref="IServiceBlueprintSourceStore.SaveAsync"/>). Pass <c>0</c> for
    /// a blueprint you expect doesn't exist yet.
    /// </summary>
    public async Task<ServiceBlueprintSaveOutcome> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default)
    {
        var validation = Validate(blueprint);
        if (!validation.IsValid)
        {
            return ServiceBlueprintSaveOutcome.Invalid(validation.Diagnostics);
        }

        var result = await store.SaveAsync(blueprint, expectedVersion, ct);
        return result.Saved
            ? ServiceBlueprintSaveOutcome.Saved(result.CurrentVersion)
            : ServiceBlueprintSaveOutcome.Conflict(result.CurrentVersion);
    }

    public ServiceBlueprintSimulationResult Simulate(
        ServiceBlueprint blueprint,
        IReadOnlyList<ProcessManagerSimulationStep> steps,
        IReadOnlyDictionary<string, object?>? mockServiceInputs = null) =>
        new ServiceBlueprintSimulationRunner().Run(blueprint, steps, mockServiceInputs);
}
