namespace Wayfinder.Services;

/// <summary>
/// Shared bound on any regex evaluated against blueprint-author-supplied patterns —
/// <see cref="Validation.FieldValueValidator"/>'s <c>pattern</c> constraint and
/// <see cref="Calculations.CalculationEvaluator"/>'s <c>matches()</c> function both apply this
/// same timeout, so the two can never drift into different tolerances for the same class of
/// risk (a pathological pattern causing catastrophic backtracking). Both call sites only ever
/// run a pattern the blueprint author wrote at design time against end-user-submitted text —
/// the pattern itself is never attacker-controlled, only the text being matched is, so this
/// timeout exists to fail a slow pattern closed rather than hang a request thread, not to guard
/// against a malicious pattern.
/// </summary>
public static class RegexPolicy
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
}
