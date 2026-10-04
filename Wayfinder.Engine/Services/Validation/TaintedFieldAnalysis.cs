using System.Text.RegularExpressions;
using Wayfinder.Models.ServiceDesign.Calculations;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// Which calculated fields and series can never be evaluated statically because they depend, directly
/// or through another field, on something static validation has no value for. Such a field was always
/// going to fail evaluation too — that is expected, not an authoring mistake.
/// </summary>
internal static partial class TaintedFieldAnalysis
{
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*")]
    private static partial Regex WordPattern();

    /// <summary>
    /// Fixed-point closure: starting from the genuinely-unresolvable <paramref name="roots"/> (an
    /// unresolved service field, an unresolved shape path, a numeric input with no default),
    /// repeatedly adds every other declared field/series whose own expression text mentions a name
    /// any prefix of which (see <see cref="DottedNames.ContainsAnyPrefix"/>) is already tainted,
    /// until nothing new is found.
    /// </summary>
    public static HashSet<string> Closure(ServiceBlueprintCalculationSet? calculations, IEnumerable<string> roots)
    {
        var tainted = new HashSet<string>(roots, StringComparer.Ordinal);
        if (calculations is null)
        {
            return tainted;
        }

        var declared = ExpressionsByName(calculations).ToList();
        while (true)
        {
            var fresh = declared
                .Where(entry => !tainted.Contains(entry.Name) && entry.Expressions.Any(expression => ReferencesTainted(expression, tainted)))
                .Select(entry => entry.Name)
                .ToList();
            if (fresh.Count == 0)
            {
                return tainted;
            }

            tainted.UnionWith(fresh);
        }
    }

    private static IEnumerable<(string Name, IEnumerable<string> Expressions)> ExpressionsByName(ServiceBlueprintCalculationSet calculations)
    {
        foreach (var (name, field) in calculations.Fields)
        {
            yield return (name, field.Expr is null ? [] : [field.Expr]);
        }

        foreach (var (name, series) in calculations.Series ?? new Dictionary<string, ServiceBlueprintCalculationSeries>())
        {
            yield return (name, new[] { series.From, series.To }.Concat(series.Values.Values));
        }
    }

    private static bool ReferencesTainted(string expression, IReadOnlySet<string> tainted) =>
        WordPattern().Matches(expression).Any(match => DottedNames.ContainsAnyPrefix(tainted, match.Value));
}
