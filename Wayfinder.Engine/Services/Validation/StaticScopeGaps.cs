using System.Text.RegularExpressions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// What static validation has no real value for — and so what an expression that references one is
/// <em>expected</em> to fail on. Validation has no submitted data, but
/// <see cref="CalculationScopeBuilder.Build"/> gives every string/boolean input a safe placeholder
/// regardless, so a missing default no longer makes those "Unknown" here. Three things can still be
/// genuinely unresolvable: a numeric input with no declared default (0 is a real value, not a safe
/// stand-in), a <c>source: "service"</c> field (or one property of its <c>shape</c>) with nothing to
/// stand in for the host's value, and every field or series that transitively depends on one of
/// those (<see cref="TaintedFieldNames"/>). <see cref="Classify"/> downgrades an evaluation failure
/// caused by any of them to a Warning that says how to fix it; anything else is an error.
/// </summary>
internal sealed partial class StaticScopeGaps
{
    private StaticScopeGaps(
        IReadOnlySet<string> numericInputsWithoutDefault,
        StaticServiceInputs serviceInputs,
        IReadOnlySet<string> taintedFieldNames)
    {
        NumericInputsWithoutDefault = numericInputsWithoutDefault;
        UnresolvedServiceFields = serviceInputs.UnresolvedFields;
        UnresolvedServiceShapePaths = serviceInputs.UnresolvedShapePaths;
        TaintedFieldNames = taintedFieldNames;
    }

    public IReadOnlySet<string> NumericInputsWithoutDefault { get; }
    public IReadOnlySet<string> UnresolvedServiceFields { get; }
    public IReadOnlySet<string> UnresolvedServiceShapePaths { get; }
    public IReadOnlySet<string> TaintedFieldNames { get; }

    public static StaticScopeGaps For(ServiceBlueprint blueprint, StaticServiceInputs serviceInputs)
    {
        // "" / false is a safe placeholder for a missing string/checkbox, but 0 is a real, meaningful
        // number, not "nothing yet".
        var numericInputsWithoutDefault = CalculationScopeBuilder.DescribeInputs(blueprint)
            .Where(input => input.Value.Type == "number" && string.IsNullOrWhiteSpace(input.Value.Default))
            .Select(input => input.Key)
            .ToHashSet(StringComparer.Ordinal);

        var roots = numericInputsWithoutDefault.Concat(serviceInputs.UnresolvedFields).Concat(serviceInputs.UnresolvedShapePaths);
        return new StaticScopeGaps(numericInputsWithoutDefault, serviceInputs, TaintedFieldAnalysis.Closure(blueprint.Calculations, roots));
    }

    [GeneratedRegex(@"^Unknown name '([^']+)' in")]
    private static partial Regex UnknownNamePattern();

    /// <summary>
    /// Turns a failed static evaluation into an error, unless a known gap explains it, in which case
    /// it becomes an "unverified" warning with the explanation appended. <paramref name="subjectName"/>
    /// is the field/series being evaluated when the failure is about one (the calculation pass); it is
    /// null for a showWhen/route/stage-validation expression, which has no name of its own.
    /// </summary>
    public ServiceBlueprintDiagnostic Classify(EvaluationCodes codes, string path, string message, string? subjectName = null)
    {
        var name = UnknownNamePattern().Match(message) is { Success: true } match ? match.Groups[1].Value : null;
        var explanation = Explain(subjectName, name);

        return explanation is null
            ? new ServiceBlueprintDiagnostic(codes.Error, path, message)
            : new ServiceBlueprintDiagnostic(codes.Unverified, path, $"{message} {explanation}", ServiceBlueprintDiagnosticSeverity.Warning);
    }

    /// <summary>The first matching reason an expression cannot be verified, most specific first, or null if none applies.</summary>
    private string? Explain(string? subject, string? name) =>
        Reasons(subject, name).FirstOrDefault(reason => reason.Applies).Text?.Invoke();

    private IEnumerable<(bool Applies, Func<string> Text)> Reasons(string? subject, string? name)
    {
        var root = name?.Split('.')[0];
        bool Contains(IReadOnlySet<string> set) => name is not null && (set.Contains(name) || set.Contains(root!));
        bool HasPrefixIn(IReadOnlySet<string> set) => name is not null && DottedNames.ContainsAnyPrefix(set, name);

        yield return (
            subject is not null && TaintedFieldNames.Contains(subject) && !NumericInputsWithoutDefault.Contains(subject) && !UnresolvedServiceFields.Contains(subject),
            () => DependsOnGap(subject!));

        yield return (
            Contains(NumericInputsWithoutDefault),
            () => $"'{name}' is a real numeric input field on this blueprint, but it has no " +
                "declared \"default\" value and there's no real submitted data to fall back on outside a live " +
                "instance — unlike text/checkbox fields, there's no safe placeholder for a missing number, so " +
                "validate_service_blueprint can't verify this expression statically. Add a default to that " +
                "component to verify it here, or use simulate_service_blueprint with real field values instead.");

        yield return (
            Contains(UnresolvedServiceFields),
            () => $"'{name}' is a source: \"service\" field with no \"valueKind\"/\"default\"/\"shape\" " +
                "for validate to stand in for the host's value, so this expression can't be verified " +
                "statically. Declare its valueKind (a \"number\" also needs a \"default\"), or shape if it's " +
                "an object, pass mockServiceInputs, or use simulate_service_blueprint.");

        // A prefix match, not an exact one: the failing path names the FULL original expression path
        // ("member.address.postcode"), which can be longer than the specific unresolved property
        // ("member.address") when resolution stops partway through a chain.
        yield return (
            HasPrefixIn(UnresolvedServiceShapePaths),
            () => $"'{name}' is a property of a service-sourced field declared via \"shape\" with no " +
                "\"valueKind\"/\"default\" of its own (and no further nested \"shape\"), so this expression " +
                "can't be verified statically. Declare its valueKind (a \"number\" also needs a \"default\"), " +
                "a nested shape if it's itself an object, pass mockServiceInputs, or use " +
                "simulate_service_blueprint.");

        yield return (HasPrefixIn(TaintedFieldNames), () => DependsOnGap(name!));
    }

    private static string DependsOnGap(string who) =>
        $"'{who}' itself depends (directly or through another field) on a service field " +
        "or numeric input static validation has no real value for, so this expression can't be " +
        "verified statically either. Declare the underlying field's valueKind/default (or shape, " +
        "for an object field), pass mockServiceInputs, or use simulate_service_blueprint.";
}
