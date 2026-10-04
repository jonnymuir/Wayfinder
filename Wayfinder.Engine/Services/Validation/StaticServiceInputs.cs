using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// The <c>source: "service"</c> values static validation evaluates against, and the service fields
/// (and individual properties of a declared <c>shape</c>) it still has nothing for. Precedence per
/// field: an explicit mock service input wins; then a declared <c>default</c> parsed per
/// <c>valueKind</c>; then, for a <c>valueKind</c> of "string"/"boolean" with no default, the same
/// safe placeholder ("" / false) <see cref="CalculationScopeBuilder"/> gives an unfilled input of
/// that kind; then a declared <c>shape</c> for a field the host hands back as an object rather than
/// a scalar — built into a real nested dictionary, the shape <c>CalculationEvaluator.ResolvePath</c>
/// expects at runtime, so a dotted path like <c>member.tier</c> resolves against it. A field with
/// none of those stays unresolved.
/// </summary>
internal sealed record StaticServiceInputs(
    IReadOnlyDictionary<string, object?> Resolved,
    IReadOnlySet<string> UnresolvedFields,
    IReadOnlySet<string> UnresolvedShapePaths)
{
    public static StaticServiceInputs Build(
        ServiceBlueprintCalculationSet? calculations,
        IReadOnlyDictionary<string, object?>? mockServiceInputs)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        var unresolvedFields = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedShapePaths = new HashSet<string>(StringComparer.Ordinal);

        var serviceFields = calculations?.Fields.Where(field => string.Equals(field.Value.Source, "service", StringComparison.OrdinalIgnoreCase));
        foreach (var (name, field) in serviceFields ?? [])
        {
            if (mockServiceInputs is not null && mockServiceInputs.TryGetValue(name, out var mocked))
            {
                resolved[name] = mocked;
            }
            else if (TryResolvePlaceholder(name, field.ValueKind, field.Default, field.Shape, unresolvedShapePaths, out var placeholder))
            {
                resolved[name] = placeholder;
            }
            else
            {
                unresolvedFields.Add(name);
            }
        }

        return new StaticServiceInputs(resolved, unresolvedFields, unresolvedShapePaths);
    }

    /// <summary>One warning per service field, and per shape property, that static validation has nothing to stand in for.</summary>
    public IEnumerable<ServiceBlueprintDiagnostic> UnverifiedWarnings()
    {
        foreach (var name in UnresolvedFields)
        {
            yield return new ServiceBlueprintDiagnostic(
                "CALC_SERVICE_FIELD_UNVERIFIED",
                $"calculations.fields.{name}",
                $"Field '{name}' is service-sourced and validate has no value to stand in for it, so " +
                "expressions that read it can't be checked here (they're reported as unverified below, " +
                "not as errors). Declare \"valueKind\" (\"string\"/\"boolean\" gets a safe placeholder; " +
                "\"number\" also needs a \"default\"), or, for an object the host hands back whole " +
                "(e.g. a member record), declare \"shape\" instead. Otherwise pass mockServiceInputs, " +
                "or use simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }

        // The finer-grained counterpart: a field declared via "shape" is itself resolved (a real,
        // if partial, object), so it is never an unresolved field — but one of ITS OWN properties
        // can still have neither a valueKind/default nor its own nested shape.
        foreach (var dottedPath in UnresolvedShapePaths)
        {
            yield return new ServiceBlueprintDiagnostic(
                "CALC_SERVICE_FIELD_SHAPE_LEAF_UNVERIFIED",
                $"calculations.fields.{DottedNames.JsonShapePath(dottedPath)}",
                $"'{dottedPath}' is a property of a service-sourced field declared via \"shape\" with no " +
                "\"valueKind\"/\"default\" of its own (and no further nested \"shape\"), so expressions that " +
                "read it can't be checked here (they're reported as unverified below, not as errors). " +
                "Declare its valueKind (\"string\"/\"boolean\" gets a safe placeholder; \"number\" also needs " +
                "a \"default\"), a nested shape if it's itself an object, pass mockServiceInputs, or use " +
                "simulate_service_blueprint.",
                ServiceBlueprintDiagnosticSeverity.Warning);
        }
    }

    /// <summary>
    /// The placeholder for one field or shape property: a scalar from its declared kind/default,
    /// else an object built from its nested <c>shape</c>. Within a shape, a property with neither is
    /// omitted from the object and its full dotted path recorded in
    /// <paramref name="unresolvedShapePaths"/> by the caller of the recursion — a reference to that
    /// specific path still fails evaluation as expected without blocking its siblings.
    /// </summary>
    private static bool TryResolvePlaceholder(
        string path,
        string? valueKind,
        string? @default,
        IReadOnlyDictionary<string, ServiceBlueprintCalculationFieldShape>? shape,
        ISet<string> unresolvedShapePaths,
        out object? placeholder)
    {
        var kind = valueKind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(@default) && kind is "number" or "string" or "boolean")
        {
            placeholder = CalculationScopeBuilder.CoerceScalar(@default!, kind);
            return true;
        }

        if (kind is "string" or "boolean")
        {
            placeholder = kind == "boolean" ? false : string.Empty;
            return true;
        }

        if (shape is { Count: > 0 })
        {
            placeholder = BuildShape(path, shape, unresolvedShapePaths);
            return true;
        }

        placeholder = null;
        return false;
    }

    private static Dictionary<string, object?> BuildShape(
        string path,
        IReadOnlyDictionary<string, ServiceBlueprintCalculationFieldShape> shape,
        ISet<string> unresolvedShapePaths)
    {
        var built = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (propertyName, property) in shape)
        {
            var propertyPath = $"{path}.{propertyName}";
            if (TryResolvePlaceholder(propertyPath, property.ValueKind, property.Default, property.Shape, unresolvedShapePaths, out var value))
            {
                built[propertyName] = value;
            }
            else
            {
                unresolvedShapePaths.Add(propertyPath);
            }
        }

        return built;
    }
}
