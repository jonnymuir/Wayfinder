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
        diagnostics.AddRange(blueprint.ValidateRequestPolicy());
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

        // Which field names are genuinely unresolvable at authoring time (no real submission, no
        // safe stand-in) — a reference to one of these fails static evaluation as expected, so
        // ClassifyEvalDiagnostic downgrades that specific failure to a Warning rather than an
        // error. Two sources:
        //  - numeric input fields with no declared "default": "" / false is a safe placeholder for
        //    a missing string/checkbox, but 0 is a real, meaningful number, not "nothing yet".
        //  - source: "service" calc fields with neither a mockServiceInput nor a declared
        //    valueKind (+ default for a number) or Shape to stand in for the host's real value.
        var numericInputsWithoutDefault = CalculationScopeBuilder.DescribeInputs(blueprint)
            .Where(kvp => kvp.Value.Type == "number" && string.IsNullOrWhiteSpace(kvp.Value.Default))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        var (staticServiceInputs, unresolvedServiceFields, unresolvedServiceShapePaths) =
            BuildStaticServiceInputs(blueprint.Calculations, mockServiceInputs);
        var gaps = new StaticScopeGaps(
            numericInputsWithoutDefault,
            unresolvedServiceFields,
            unresolvedServiceShapePaths,
            ComputeTaintedFieldNames(blueprint.Calculations, numericInputsWithoutDefault, unresolvedServiceFields, unresolvedServiceShapePaths));

        foreach (var name in unresolvedServiceFields)
        {
            diagnostics.Add(new ServiceBlueprintDiagnostic(
                "CALC_SERVICE_FIELD_UNVERIFIED",
                $"calculations.fields.{name}",
                $"Field '{name}' is service-sourced and validate has no value to stand in for it, so " +
                "expressions that read it can't be checked here (they're reported as unverified below, " +
                "not as errors). Declare \"valueKind\" (\"string\"/\"boolean\" gets a safe placeholder; " +
                "\"number\" also needs a \"default\"), or, for an object the host hands back whole " +
                "(e.g. a member record), declare \"shape\" instead. Otherwise pass mockServiceInputs, " +
                "or use simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning));
        }

        // The finer-grained counterpart to the loop above: a field declared via "shape" is itself
        // resolved (it's in staticServiceInputs as a real, if partial, object), so it never reaches
        // unresolvedServiceFields — but one of ITS OWN properties can still have neither a
        // valueKind/default nor its own nested shape, leaving exactly that dotted path
        // unverifiable. dottedPath here is the JSON structure ("member.address"); JsonShapePath
        // rewrites it to where that property actually lives ("member.shape.address").
        foreach (var dottedPath in unresolvedServiceShapePaths)
        {
            diagnostics.Add(new ServiceBlueprintDiagnostic(
                "CALC_SERVICE_FIELD_SHAPE_LEAF_UNVERIFIED",
                $"calculations.fields.{JsonShapePath(dottedPath)}",
                $"'{dottedPath}' is a property of a service-sourced field declared via \"shape\" with no " +
                "\"valueKind\"/\"default\" of its own (and no further nested \"shape\"), so expressions that " +
                "read it can't be checked here (they're reported as unverified below, not as errors). " +
                "Declare its valueKind (\"string\"/\"boolean\" gets a safe placeholder; \"number\" also needs " +
                "a \"default\"), a nested shape if it's itself an object, pass mockServiceInputs, or use " +
                "simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning));
        }

        IReadOnlyDictionary<string, object?> showWhenScope;

        if (blueprint.Calculations is null)
        {
            showWhenScope = CalculationScopeBuilder.Build(blueprint, EmptyFieldValues, mockServiceInputs);
        }
        else
        {
            var scope = CalculationScopeBuilder.Build(blueprint, EmptyFieldValues, staticServiceInputs);
            var evaluation = evaluator.EvaluateCollectingErrors(blueprint.Calculations, scope);
            foreach (var fieldOrSeries in evaluation.Diagnostics)
            {
                var (errorCode, unverifiedCode, path) = fieldOrSeries.Kind == CalculationDiagnosticKind.Field
                    ? ("CALC_FIELD_ERROR", "CALC_FIELD_UNVERIFIED", $"calculations.fields.{fieldOrSeries.Name}")
                    : ("CALC_SERIES_ERROR", "CALC_SERIES_UNVERIFIED", $"calculations.series.{fieldOrSeries.Name}");
                diagnostics.Add(ClassifyEvalDiagnostic(
                    errorCode, unverifiedCode, path, fieldOrSeries.Message, gaps, subjectName: fieldOrSeries.Name));
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
                        "SHOW_WHEN_EVAL_ERROR", "SHOW_WHEN_UNVERIFIED", $"{path}.showWhen", ex.Message, gaps));
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
                        gaps, diagnostics);
                }

                CheckStageValidationExpression(
                    evaluator, rule.Rule, showWhenScope, blueprint.Calculations,
                    "STAGE_VALIDATION_RULE_EVAL_ERROR", "STAGE_VALIDATION_RULE_UNVERIFIED", $"{path}.rule",
                    gaps, diagnostics);
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
                        ex.Message, gaps));
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
        StaticScopeGaps gaps,
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
            diagnostics.Add(ClassifyEvalDiagnostic(errorCode, unverifiedCode, path, ex.Message, gaps));
        }
    }

    /// <summary>
    /// Field names — and, since <c>shape</c> exists, individual dotted property paths within an
    /// otherwise-resolved service field's shape (e.g. <c>member.age</c>, when <c>member</c> itself
    /// resolves but that one property has no <c>valueKind</c>/<c>default</c>/nested <c>shape</c> of
    /// its own) — that static validation has no real value for, so an expression that references
    /// one is expected to fail evaluation. <see cref="ClassifyEvalDiagnostic"/> reports that as
    /// "unverified" (a Warning) rather than an error, with a message tailored to why. See
    /// <see cref="Validate"/> where this is built. <see cref="TaintedFieldNames"/> is the
    /// transitive closure of every other calculated field/series whose own expression references
    /// one of these gaps (directly or through another tainted field) — evaluating one of those was
    /// always going to fail too, so it gets the same "unverified" treatment as the root cause,
    /// whether or not its own failure message happens to name the root cause (see
    /// <see cref="ComputeTaintedFieldNames"/>).
    /// </summary>
    private readonly record struct StaticScopeGaps(
        IReadOnlySet<string> NumericInputsWithoutDefault,
        IReadOnlySet<string> UnresolvedServiceFields,
        IReadOnlySet<string> UnresolvedServiceShapePaths,
        IReadOnlySet<string> TaintedFieldNames);

    private static readonly Regex WordPattern = new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

    /// <summary>
    /// Fixed-point closure: starting from the genuinely-unresolvable root names/paths (an
    /// unresolved service field, an unresolved shape leaf path, a numeric input with no default),
    /// repeatedly scans every other declared calculated field/series' own expression text for a
    /// token any PREFIX of which (see <see cref="ContainsAnyPrefix"/> — a dotted reference like
    /// <c>member.age</c> checks both <c>member</c> and <c>member.age</c>, since either grain can be
    /// the tainted one) is already known-tainted, adding its name to the set until nothing new is
    /// found. A field that references a tainted field/path, directly or transitively, was always
    /// going to fail evaluation too — that is expected, not an authoring mistake.
    /// </summary>
    private static IReadOnlySet<string> ComputeTaintedFieldNames(
        ServiceBlueprintCalculationSet? calculations,
        IReadOnlySet<string> numericInputsWithoutDefault,
        IReadOnlySet<string> unresolvedServiceFields,
        IReadOnlySet<string> unresolvedServiceShapePaths)
    {
        var tainted = new HashSet<string>(numericInputsWithoutDefault, StringComparer.Ordinal);
        tainted.UnionWith(unresolvedServiceFields);
        tainted.UnionWith(unresolvedServiceShapePaths);

        if (calculations is null)
        {
            return tainted;
        }

        bool changed;
        do
        {
            changed = false;

            foreach (var (name, field) in calculations.Fields)
            {
                if (tainted.Contains(name)) continue;
                if (field.Expr is not null && ExpressionReferencesTainted(field.Expr, tainted))
                {
                    tainted.Add(name);
                    changed = true;
                }
            }

            foreach (var (name, series) in calculations.Series ?? new Dictionary<string, ServiceBlueprintCalculationSeries>())
            {
                if (tainted.Contains(name)) continue;
                if (ExpressionReferencesTainted(series.From, tainted)
                    || ExpressionReferencesTainted(series.To, tainted)
                    || series.Values.Values.Any(expr => ExpressionReferencesTainted(expr, tainted)))
                {
                    tainted.Add(name);
                    changed = true;
                }
            }
        } while (changed);

        return tainted;
    }

    private static bool ExpressionReferencesTainted(string expression, IReadOnlySet<string> tainted)
    {
        foreach (Match match in WordPattern.Matches(expression))
        {
            if (ContainsAnyPrefix(tainted, match.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if <paramref name="dottedName"/> itself, or any of its dot-separated prefixes (walked
    /// shortest to longest — "member", then "member.age", then "member.age.foo", ...), is in
    /// <paramref name="names"/>. A gap can be tracked at either grain: a whole unresolved field
    /// ("member") or one specific unresolved property within an otherwise-resolved shape
    /// ("member.age") — a reference to a name at or past either point is equally expected to fail.
    /// </summary>
    private static bool ContainsAnyPrefix(IReadOnlySet<string> names, string dottedName)
    {
        var segments = dottedName.Split('.');
        var prefix = segments[0];
        if (names.Contains(prefix))
        {
            return true;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            prefix = $"{prefix}.{segments[i]}";
            if (names.Contains(prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites a dotted service-field path ("member.address.postcode") to where that property
    /// actually lives in the JSON ("member.shape.address.shape.postcode") — every segment past the
    /// first is nested one level deeper under its own "shape" key.
    /// </summary>
    private static string JsonShapePath(string dottedName) => string.Join(".shape.", dottedName.Split('.'));

    /// <summary>
    /// Builds the <c>source: "service"</c> values static validation evaluates against, and the set
    /// of service fields it still has nothing for. Precedence per field: an explicit
    /// <paramref name="mockServiceInputs"/> entry wins; then a declared <c>default</c> parsed per
    /// <c>valueKind</c>; then, for a <c>valueKind</c> of "string"/"boolean" with no default, the
    /// same safe placeholder ("" / false) <see cref="CalculationScopeBuilder"/> already gives an
    /// unfilled input of that kind; then a declared <c>shape</c> (see
    /// <see cref="ResolveShapePlaceholder"/>) for a field the host hands back as an object rather
    /// than a scalar — built into a real nested dictionary, the same shape
    /// <c>CalculationEvaluator.ResolvePath</c> expects at runtime, so a dotted-path expression like
    /// <c>member.tier</c> resolves against it instead of failing static evaluation. A field with
    /// none of the above — no mock, no scalar <c>valueKind</c>, no <c>shape</c> — stays unresolved.
    /// </summary>
    private static (Dictionary<string, object?> Resolved, IReadOnlySet<string> UnresolvedFields, IReadOnlySet<string> UnresolvedShapePaths) BuildStaticServiceInputs(
        ServiceBlueprintCalculationSet? calculations,
        IReadOnlyDictionary<string, object?>? mockServiceInputs)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        var unresolvedFields = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedShapePaths = new HashSet<string>(StringComparer.Ordinal);
        if (calculations is null)
        {
            return (resolved, unresolvedFields, unresolvedShapePaths);
        }

        foreach (var (name, field) in calculations.Fields)
        {
            if (!string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (mockServiceInputs is not null && mockServiceInputs.TryGetValue(name, out var mocked))
            {
                resolved[name] = mocked;
                continue;
            }

            var scalar = ResolveScalarPlaceholder(field.ValueKind, field.Default);
            if (scalar.Resolved)
            {
                resolved[name] = scalar.Value;
                continue;
            }

            if (field.Shape is { Count: > 0 })
            {
                resolved[name] = ResolveShapePlaceholder(name, field.Shape, unresolvedShapePaths);
                continue;
            }

            unresolvedFields.Add(name);
        }

        return (resolved, unresolvedFields, unresolvedShapePaths);
    }

    /// <summary>
    /// The single-property version of the "declared kind/default → safe placeholder" rule
    /// <see cref="BuildStaticServiceInputs"/> applies to a top-level service field — factored out
    /// so <see cref="ResolveShapePlaceholder"/> can apply the exact same precedence to each leaf of
    /// a declared <see cref="ServiceBlueprintCalculationField.Shape"/> without duplicating it.
    /// </summary>
    private static (bool Resolved, object? Value) ResolveScalarPlaceholder(string? valueKind, string? @default)
    {
        var kind = valueKind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(@default) && kind is "number" or "string" or "boolean")
        {
            return (true, CalculationScopeBuilder.CoerceScalar(@default!, kind));
        }

        if (kind is "string" or "boolean")
        {
            return (true, kind == "boolean" ? false : string.Empty);
        }

        return (false, null);
    }

    /// <summary>
    /// Recursively builds the placeholder object a declared <c>shape</c> describes — a leaf
    /// property resolves via <see cref="ResolveScalarPlaceholder"/> exactly like a top-level
    /// service field would; a property with its own nested <c>shape</c> recurses.
    /// <paramref name="path"/> is the dotted path to the object being built (e.g. <c>"member"</c>,
    /// or <c>"member.address"</c> one level down) — a property with neither a scalar kind nor a
    /// nested shape is omitted from the built object AND its own full dotted path
    /// (<paramref name="path"/> + "." + the property name) is added to
    /// <paramref name="unresolvedShapePaths"/>, the same "stays unresolvable" outcome an
    /// under-declared top-level field has, just at the finer grain of one property rather than the
    /// whole field — a reference to that specific path still fails evaluation as expected, it just
    /// doesn't block every OTHER property this same object declares.
    /// </summary>
    private static Dictionary<string, object?> ResolveShapePlaceholder(
        string path,
        IReadOnlyDictionary<string, ServiceBlueprintCalculationFieldShape> shape,
        HashSet<string> unresolvedShapePaths)
    {
        var placeholder = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (propertyName, property) in shape)
        {
            var propertyPath = $"{path}.{propertyName}";
            var scalar = ResolveScalarPlaceholder(property.ValueKind, property.Default);
            if (scalar.Resolved)
            {
                placeholder[propertyName] = scalar.Value;
                continue;
            }

            if (property.Shape is { Count: > 0 })
            {
                placeholder[propertyName] = ResolveShapePlaceholder(propertyPath, property.Shape, unresolvedShapePaths);
                continue;
            }

            unresolvedShapePaths.Add(propertyPath);
        }

        return placeholder;
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
    /// Validation has no real submitted data, but <see cref="CalculationScopeBuilder.Build"/>
    /// gives every string/boolean input a safe placeholder ("" / false) regardless — a missing
    /// default no longer makes those fields "Unknown" here. Two cases can still be genuinely
    /// unresolvable, and referencing one is expected to fail static evaluation rather than being
    /// an authoring mistake: a numeric input with no declared default (0 is a real value, not a
    /// safe stand-in), and a <c>source: "service"</c> field with no <c>valueKind</c>/<c>default</c>
    /// to stand in for the host's value. A third case covers every field/series that itself
    /// transitively depends on one of those two (<see cref="StaticScopeGaps.TaintedFieldNames"/>) —
    /// <paramref name="subjectName"/> is that field/series' own name, when the diagnostic being
    /// classified is about one (the calc-field/series pass; <see langword="null"/> for a
    /// showWhen/route/stage-validation expression, which has no field name of its own). Downgrade
    /// all three cases to a Warning with a message pointing at the fix; anything else genuinely is
    /// an error.
    /// </summary>
    private static ServiceBlueprintDiagnostic ClassifyEvalDiagnostic(
        string errorCode,
        string unverifiedCode,
        string path,
        string message,
        StaticScopeGaps gaps,
        string? subjectName = null)
    {
        if (subjectName is not null && gaps.TaintedFieldNames.Contains(subjectName)
            && !gaps.NumericInputsWithoutDefault.Contains(subjectName)
            && !gaps.UnresolvedServiceFields.Contains(subjectName))
        {
            return new ServiceBlueprintDiagnostic(
                unverifiedCode,
                path,
                $"{message} '{subjectName}' itself depends (directly or through another field) on a " +
                "service field or numeric input static validation has no real value for, so this expression " +
                "can't be verified statically either. Declare the underlying field's valueKind/default (or " +
                "shape, for an object field), pass mockServiceInputs, or use simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        var match = UnknownNamePattern.Match(message);
        var name = match.Success ? match.Groups[1].Value : null;
        var root = name?.Split('.')[0];

        if (name is not null && (gaps.NumericInputsWithoutDefault.Contains(name) || gaps.NumericInputsWithoutDefault.Contains(root!)))
        {
            return new ServiceBlueprintDiagnostic(
                unverifiedCode,
                path,
                $"{message} '{name}' is a real numeric input field on this blueprint, but it has no " +
                "declared \"default\" value and there's no real submitted data to fall back on outside a live " +
                "instance — unlike text/checkbox fields, there's no safe placeholder for a missing number, so " +
                "validate_service_blueprint can't verify this expression statically. Add a default to that " +
                "component to verify it here, or use simulate_service_blueprint with real field values instead.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        if (name is not null && (gaps.UnresolvedServiceFields.Contains(name) || gaps.UnresolvedServiceFields.Contains(root!)))
        {
            return new ServiceBlueprintDiagnostic(
                unverifiedCode,
                path,
                $"{message} '{name}' is a source: \"service\" field with no \"valueKind\"/\"default\"/\"shape\" " +
                "for validate to stand in for the host's value, so this expression can't be verified " +
                "statically. Declare its valueKind (a \"number\" also needs a \"default\"), or shape if it's " +
                "an object, pass mockServiceInputs, or use simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        // A field that resolves overall (it's in staticServiceInputs — see BuildStaticServiceInputs)
        // via a declared shape, but ONE of its own properties has neither valueKind/default nor a
        // further nested shape — checked via ContainsAnyPrefix (not just an exact match) because
        // the failing ResolvePath's own message names the FULL original expression path (e.g.
        // "member.address.postcode"), which can be longer than the specific unresolved property
        // ("member.address") when resolution stops partway through a chain.
        if (name is not null && ContainsAnyPrefix(gaps.UnresolvedServiceShapePaths, name))
        {
            return new ServiceBlueprintDiagnostic(
                unverifiedCode,
                path,
                $"{message} '{name}' is a property of a service-sourced field declared via \"shape\" with no " +
                "\"valueKind\"/\"default\" of its own (and no further nested \"shape\"), so this expression " +
                "can't be verified statically. Declare its valueKind (a \"number\" also needs a \"default\"), " +
                "a nested shape if it's itself an object, pass mockServiceInputs, or use " +
                "simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        if (name is not null && ContainsAnyPrefix(gaps.TaintedFieldNames, name))
        {
            return new ServiceBlueprintDiagnostic(
                unverifiedCode,
                path,
                $"{message} '{name}' itself depends (directly or through another field) on a service field " +
                "or numeric input static validation has no real value for, so this expression can't be " +
                "verified statically either. Declare the underlying field's valueKind/default (or shape, " +
                "for an object field), pass mockServiceInputs, or use simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        return new ServiceBlueprintDiagnostic(errorCode, path, message);
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
