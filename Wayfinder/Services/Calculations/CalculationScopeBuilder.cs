using System.Globalization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Services.Calculations;

/// <summary>
/// Builds the typed evaluation scope for a blueprint's calculation set from raw instance
/// field values. Types and defaults come from the definition's own input components:
/// slider/number/decimal fields parse as decimals (tolerating "£" and thousands
/// separators written back for display), boolean fields parse as real booleans, everything
/// else stays a string. Service-sourced values supplied by the host are merged in last.
///
/// The TypeScript live-form runtime applies the same rules client-side; keep the two in
/// step (the input-type map is shipped to the client in the live model for that reason).
/// </summary>
public static class CalculationScopeBuilder
{
    /// <summary>Returns fieldKey → ("number" | "boolean" | "string", default) for every input in the definition.</summary>
    public static IReadOnlyDictionary<string, (string Type, string? Default)> DescribeInputs(
        ServiceBlueprint definition)
    {
        var inputs = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var stage in definition.Stages)
        {
            foreach (var input in stage.Components.GetSubmittableInputs())
            {
                if (string.IsNullOrWhiteSpace(input.FieldKey) || inputs.ContainsKey(input.FieldKey))
                {
                    continue;
                }

                var type = input switch
                {
                    SliderComponent or NumberInputComponent or DecimalInputComponent => "number",
                    BooleanComponent => "boolean",
                    _ => "string",
                };
                inputs[input.FieldKey] = (type, input.Default);
            }
        }

        return inputs;
    }

    public static Dictionary<string, object?> Build(
        ServiceBlueprint definition,
        IReadOnlyDictionary<string, object?> fieldValues,
        IReadOnlyDictionary<string, object?>? serviceInputs = null)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (fieldKey, (type, defaultValue)) in DescribeInputs(definition))
        {
            var raw = fieldValues.TryGetValue(fieldKey, out var saved) ? saved?.ToString() : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = defaultValue;
            }

            if (raw is null)
            {
                // Absent (no real submission, no declared default) is not the same as unknown —
                // the field is genuinely declared somewhere in the blueprint, it just has no
                // value in THIS evaluation context (most commonly: authoring-time validation,
                // which never has a real citizen behind it). For "string"/"boolean" fields there
                // is a genuinely safe, natural "nothing here" value — an unfilled text box already
                // means "" everywhere else in this system, an unticked checkbox already means
                // false — so use it rather than leaving the name unresolvable. Deliberately NOT
                // extended to "number": there is no equally safe placeholder for a missing amount
                // (0 is a real, meaningful value a service might act on), so a numeric field still
                // requires a real submission or an explicit default — see
                // ServiceBlueprintAuthoringService.Validate's narrower, Warning-severity handling
                // of exactly this case.
                if (type == "number")
                {
                    continue;
                }

                scope[fieldKey] = type == "boolean" ? false : string.Empty;
                continue;
            }

            // A "boolean"-typed input round-trips as the literal "True"/"False" once a submitted
            // C# bool is .ToString()'d above. CalculationEvaluator.ToBool requires a real bool,
            // not that string, so a bare boolean reference in showWhen/calculations would
            // otherwise always throw — parse it back to a real bool here instead.
            scope[fieldKey] = type switch
            {
                "number" => ParseNumeric(raw, fieldKey),
                "boolean" => bool.TryParse(raw, out var boolValue) ? boolValue : raw,
                _ => raw,
            };
        }

        foreach (var (key, value) in serviceInputs ?? new Dictionary<string, object?>())
        {
            scope[key] = value;
        }

        return scope;
    }

    private static object? ParseNumeric(string raw, string fieldKey)
    {
        var cleaned = raw.Replace("£", "").Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
