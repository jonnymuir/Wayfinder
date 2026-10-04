using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public partial record ServiceBlueprint
{
    /// <summary>
    /// Validates every <see cref="InputComponent"/>'s <see cref="InputComponent.ConditionalOn"/>
    /// and <see cref="InputComponent.DefaultFrom"/> against what can actually resolve them at
    /// runtime — the same "dangling binding" class of check <see cref="ValidateDataDisplayBindings"/>
    /// already applies to stat-group/summary-list fields, extended to these two properties.
    /// <see cref="InputComponent.ConditionalOn"/> must be another input field's <c>fieldKey</c>
    /// declared in the SAME stage — <c>Wayfinder/Services/Validation/FieldValueValidator.cs</c>
    /// only ever checks it against that stage's own submitted values, so a value pointing anywhere
    /// else (a typo, or a field on a different stage) leaves the field always hidden with nothing
    /// telling the author why. <see cref="InputComponent.DefaultFrom"/> must be a name declared in
    /// <see cref="Calculations"/>' <c>Fields</c> — anything else silently never resolves a default.
    /// Returns one diagnostic per dangling reference; empty list means every one resolves.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateFieldReferences()
    {
        var calculatedFieldNames = Calculations?.Fields.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        foreach (var stage in Stages)
        {
            var stageFieldKeys = stage.Components
                .GetSubmittableInputs()
                .Select(c => c.FieldKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                if (component is not InputComponent input)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(input.ConditionalOn) && !stageFieldKeys.Contains(input.ConditionalOn))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "COMPONENT_UNKNOWN_CONDITIONAL_FIELD",
                        $"{path}.conditionalOn",
                        $"'{input.Label}' is conditional on field '{input.ConditionalOn}', which isn't another " +
                        $"field's fieldKey declared in stage '{stage.StageKey}'. Visibility is only ever checked " +
                        "against the current stage's own submitted values, so this field would always stay " +
                        "hidden. Fix the fieldKey, or remove conditionalOn."));
                }

                // A dotted name (user.email) reads into an object-valued service field, so only its
                // first segment has to be a declared calculation field.
                if (!string.IsNullOrWhiteSpace(input.DefaultFrom)
                    && !calculatedFieldNames.Contains(input.DefaultFrom.Split('.')[0]))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "COMPONENT_UNKNOWN_DEFAULT_FROM",
                        $"{path}.defaultFrom",
                        $"'{input.Label}' has defaultFrom '{input.DefaultFrom}', which is not a name declared in " +
                        "this blueprint's calculations.fields — the default would never resolve. Fix the name, " +
                        "add it to calculations, or remove defaultFrom."));
                }
            }

            var validationIndex = 0;
            foreach (var rule in stage.Validations ?? [])
            {
                if (!string.IsNullOrWhiteSpace(rule.Field) && !stageFieldKeys.Contains(rule.Field))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "STAGE_VALIDATION_UNKNOWN_FIELD",
                        $"stages.{stage.StageKey}.validations[{validationIndex}].field",
                        $"Validation '{rule.Code}' has field '{rule.Field}', which isn't another field's " +
                        $"fieldKey declared in stage '{stage.StageKey}'. A failure is only ever rendered against " +
                        "the current stage's own inputs, so this would have nothing to attach to. Fix the " +
                        "fieldKey, or remove field for a stage-level (non-field) problem."));
                }

                validationIndex++;
            }
        }

        return diagnostics;
    }
}
