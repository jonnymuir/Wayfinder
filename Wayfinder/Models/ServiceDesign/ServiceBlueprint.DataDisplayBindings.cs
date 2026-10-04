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
    /// Validates that every <see cref="StatGroupComponent"/> item and <see cref="ChartComponent"/>
    /// binds to a field or series that actually exists — either a calculated field/series, or (for
    /// stat-group only) an input component's own <c>fieldKey</c> captured earlier in the blueprint.
    /// Catches the easy authoring mistake of adding a display component whose binding was never
    /// wired to the <c>calculations</c> block (or the block itself was never added), which would
    /// otherwise render silently blank with no error anywhere.
    /// Returns one diagnostic per dangling binding; empty list means every binding resolves.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateDataDisplayBindings()
    {
        var calculatedFieldNames = Calculations?.Fields.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var calculatedSeriesNames = Calculations?.Series?.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var capturedInputFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        // inputFieldKeys is the broader "known field" set DataDisplay bindings below are checked
        // against — genuinely captured inputs, plus a support-system/bulk-dataset-ingest action's
        // own outputs. The CALC_FIELD_SHADOWS_INPUT check further down deliberately uses the
        // narrower capturedInputFieldKeys instead: a service-sourced calculations.fields entry
        // legitimately shares a name with an ingest/support-system output (that's the whole point
        // of declaring one — see docs/guides/bulk-data-review.md, a showWhen expression can't
        // otherwise see it), and is never "shadowing" *user input* the way it would be if a real
        // captured field used that name — conflating the two produced a real false positive here,
        // caught by njf-contributions.json's own contributionsErrorCount field.
        var inputFieldKeys = new HashSet<string>(capturedInputFieldKeys, StringComparer.Ordinal);
        inputFieldKeys.UnionWith(GetSupportSystemOutputFieldKeys());
        inputFieldKeys.UnionWith(GetBulkDatasetIngestOutputFieldKeys());
        var stageKeys = Stages.Select(s => s.StageKey).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new List<ServiceBlueprintDiagnostic>();

        foreach (var stage in Stages)
        {
            foreach (var (component, path) in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components"))
            {
                switch (component)
                {
                    case StatGroupComponent statGroup:
                        if (statGroup.Items.Count == 0)
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_NO_ITEMS",
                                $"{path}.items",
                                $"stat-group '{statGroup.Title}' has no items — it will render nothing. " +
                                "Add at least one item bound to a captured input or calculations.fields entry."));
                        }

                        var itemIndex = 0;
                        foreach (var item in statGroup.Items)
                        {
                            if (string.IsNullOrWhiteSpace(item.FieldKey))
                            {
                                // Distinct from DATA_DISPLAY_UNKNOWN_FIELD below: this isn't a typo pointing
                                // at the wrong name, it's not pointing anywhere at all — a real regression
                                // seen in practice (an agent wired the calculation but left the display
                                // component's binding blank), and one the old "only check non-empty keys"
                                // logic silently let through.
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_MISSING_FIELD",
                                    $"{path}.items[{itemIndex}].fieldKey",
                                    $"stat-group item '{item.Label}' has no fieldKey — it can never bind to " +
                                    "anything and will always render its empty-value placeholder. Set it to a " +
                                    "captured input's fieldKey or a calculations.fields entry."));
                            }
                            else if (!calculatedFieldNames.Contains(item.FieldKey) &&
                                !inputFieldKeys.Contains(item.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_FIELD",
                                    $"{path}.items[{itemIndex}].fieldKey",
                                    $"stat-group item '{item.Label}' binds to field '{item.FieldKey}', which is " +
                                    "neither a captured input field nor a calculations.fields entry. Either add " +
                                    $"'{item.FieldKey}' to the blueprint's calculations block, or fix the fieldKey."));
                            }

                            itemIndex++;
                        }

                        break;

                    case SummaryListComponent summaryList:
                        if (summaryList.Children.Count == 0)
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_NO_ITEMS",
                                $"{path}.children",
                                $"summary-list '{summaryList.Title}' has no children — it will render nothing. " +
                                "Add at least one child bound to a captured input or calculations.fields entry."));
                        }

                        if (!string.IsNullOrWhiteSpace(summaryList.ChangeStateKey) &&
                            !stageKeys.Contains(summaryList.ChangeStateKey))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_CHANGE_STATE",
                                $"{path}.changeStateKey",
                                $"summary-list '{summaryList.Title}' changeStateKey '{summaryList.ChangeStateKey}' " +
                                "is not a stage in this blueprint — its 'Change' link would navigate nowhere. Fix " +
                                "the stage key, or remove changeStateKey if there's nothing to change."));
                        }

                        var childIndex = 0;
                        foreach (var child in summaryList.Children.OfType<InputComponent>())
                        {
                            if (string.IsNullOrWhiteSpace(child.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_MISSING_FIELD",
                                    $"{path}.children[{childIndex}].fieldKey",
                                    $"summary-list child '{child.Label}' has no fieldKey — it can never bind to " +
                                    "anything and will always render its empty-value placeholder. Set it to a " +
                                    "captured input's fieldKey or a calculations.fields entry."));
                            }
                            else if (!calculatedFieldNames.Contains(child.FieldKey) &&
                                !inputFieldKeys.Contains(child.FieldKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_FIELD",
                                    $"{path}.children[{childIndex}].fieldKey",
                                    $"summary-list child '{child.Label}' binds to field '{child.FieldKey}', which " +
                                    "is neither a captured input field nor a calculations.fields entry. Either " +
                                    $"add '{child.FieldKey}' to the blueprint's calculations block, or fix the " +
                                    "fieldKey."));
                            }

                            // A row's own ChangeStateKey (for summary lists spanning multiple earlier
                            // stages) needs the same dangling-target check as the component-level one.
                            if (!string.IsNullOrWhiteSpace(child.ChangeStateKey) &&
                                !stageKeys.Contains(child.ChangeStateKey))
                            {
                                diagnostics.Add(new ServiceBlueprintDiagnostic(
                                    "DATA_DISPLAY_UNKNOWN_CHANGE_STATE",
                                    $"{path}.children[{childIndex}].changeStateKey",
                                    $"summary-list child '{child.Label}' changeStateKey '{child.ChangeStateKey}' " +
                                    "is not a stage in this blueprint — its 'Change' link would navigate nowhere. " +
                                    "Fix the stage key, or remove changeStateKey to fall back to the summary-list's " +
                                    "own changeStateKey."));
                            }

                            childIndex++;
                        }

                        break;

                    case ChartComponent chart:
                        if (string.IsNullOrWhiteSpace(chart.Series))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_MISSING_FIELD",
                                $"{path}.series",
                                $"chart '{chart.Title}' has no series set — it can never bind to anything and " +
                                "will always render empty. Set it to a calculations.series entry."));
                        }
                        else if (!calculatedSeriesNames.Contains(chart.Series))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_FIELD",
                                $"{path}.series",
                                $"chart '{chart.Title}' binds to series '{chart.Series}', which is not a " +
                                $"calculations.series entry. Either add '{chart.Series}' to the blueprint's " +
                                "calculations block, or fix the series name."));
                        }

                        break;

                    case BulkDataReviewComponent bulkReview:
                        if (string.IsNullOrWhiteSpace(bulkReview.DatasetIdField))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_MISSING_FIELD",
                                $"{path}.datasetIdField",
                                $"bulk-data-review '{bulkReview.Title}' has no datasetIdField set — it can never " +
                                "bind to a dataset. Set it to match a bulk-dataset-ingest action's own datasetIdField."));
                        }
                        else if (!inputFieldKeys.Contains(bulkReview.DatasetIdField))
                        {
                            diagnostics.Add(new ServiceBlueprintDiagnostic(
                                "DATA_DISPLAY_UNKNOWN_FIELD",
                                $"{path}.datasetIdField",
                                $"bulk-data-review '{bulkReview.Title}' binds to datasetIdField " +
                                $"'{bulkReview.DatasetIdField}', which no bulk-dataset-ingest action in this " +
                                "blueprint declares as its own datasetIdField."));
                        }

                        break;
                }
            }
        }

        if (Calculations is not null)
        {
            foreach (var (name, field) in Calculations.Fields)
            {
                var isService = string.Equals(field.Source, "service", StringComparison.OrdinalIgnoreCase);

                if (isService && capturedInputFieldKeys.Contains(name))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "CALC_FIELD_SHADOWS_INPUT",
                        $"calculations.fields.{name}",
                        $"'{name}' is declared source: \"service\" in calculations, but a component in this " +
                        $"blueprint already captures user input under fieldKey '{name}' — that value is " +
                        "automatically in the calculation scope already. `source: \"service\"` is for a value " +
                        "an external system supplies (e.g. a lookup a host resolves), never for the user's own " +
                        "submitted input. Remove this calculations entry, or use a different field name."));
                }

                var hasValueKind = !string.IsNullOrWhiteSpace(field.ValueKind);
                var hasDefault = !string.IsNullOrWhiteSpace(field.Default);
                var hasShape = field.Shape is { Count: > 0 };
                var normalisedKind = field.ValueKind?.Trim().ToLowerInvariant();

                if ((hasValueKind || hasDefault || hasShape) && !isService)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "CALC_FIELD_VALUE_KIND_WITHOUT_SERVICE",
                        $"calculations.fields.{name}",
                        $"'{name}' declares {(hasValueKind ? "valueKind" : hasDefault ? "default" : "shape")}, " +
                        "which is only meaningful with source: \"service\" (an authoring-time aid for a value " +
                        "an external system supplies). Remove it, or add \"source\": \"service\"."));
                }
                else if (isService)
                {
                    if (hasValueKind && hasShape)
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "CALC_FIELD_SHAPE_AND_VALUE_KIND",
                            $"calculations.fields.{name}",
                            $"'{name}' declares both valueKind and shape — an object field has no single " +
                            "scalar kind. Declare one or the other."));
                    }

                    if (hasValueKind && normalisedKind is not ("number" or "string" or "boolean"))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "CALC_FIELD_INVALID_VALUE_KIND",
                            $"calculations.fields.{name}",
                            $"'{name}' declares valueKind '{field.ValueKind}'. Expected \"number\", " +
                            "\"string\" or \"boolean\", or omit it for a value with no scalar kind " +
                            "(declare \"shape\" instead for an object)."));
                    }

                    if (hasDefault && !hasValueKind)
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "CALC_FIELD_DEFAULT_WITHOUT_VALUE_KIND",
                            $"calculations.fields.{name}",
                            $"'{name}' declares a default but no valueKind — validation can't parse the " +
                            "default without knowing its kind. Add \"valueKind\": \"number\" | \"string\" | \"boolean\"."));
                    }
                    else if (hasDefault && normalisedKind == "number" &&
                             !decimal.TryParse(field.Default!.Replace("£", "").Replace(",", "").Trim(),
                                 NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "CALC_FIELD_DEFAULT_UNPARSEABLE",
                            $"calculations.fields.{name}",
                            $"'{name}' declares valueKind \"number\" but its default '{field.Default}' is not a number."));
                    }
                    else if (hasDefault && normalisedKind == "boolean" &&
                             !bool.TryParse(field.Default!.Trim(), out _))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "CALC_FIELD_DEFAULT_UNPARSEABLE",
                            $"calculations.fields.{name}",
                            $"'{name}' declares valueKind \"boolean\" but its default '{field.Default}' is not true/false."));
                    }

                    if (hasShape)
                    {
                        ValidateCalculationFieldShape($"calculations.fields.{name}", field.Shape!, diagnostics);
                    }
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The same structural checks the loop above applies to a top-level <c>source: "service"</c>
    /// field's own <c>valueKind</c>/<c>default</c> — recursed over a declared <c>shape</c>'s
    /// properties, since each one is exactly that same shape (a leaf's own valueKind/default, or a
    /// further nested shape). <paramref name="path"/> mirrors the JSON structure being validated
    /// (e.g. <c>calculations.fields.member.shape.address.shape.postcode</c>), so a diagnostic
    /// points at exactly where in a deeply-nested declaration the problem is.
    /// </summary>
    private static void ValidateCalculationFieldShape(
        string path,
        IReadOnlyDictionary<string, Calculations.ServiceBlueprintCalculationFieldShape> shape,
        List<ServiceBlueprintDiagnostic> diagnostics)
    {
        foreach (var (propertyName, property) in shape)
        {
            var propertyPath = $"{path}.shape.{propertyName}";
            var hasValueKind = !string.IsNullOrWhiteSpace(property.ValueKind);
            var hasDefault = !string.IsNullOrWhiteSpace(property.Default);
            var hasNestedShape = property.Shape is { Count: > 0 };
            var normalisedKind = property.ValueKind?.Trim().ToLowerInvariant();

            if (hasValueKind && hasNestedShape)
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "CALC_FIELD_SHAPE_AND_VALUE_KIND",
                    propertyPath,
                    $"'{propertyPath}' declares both valueKind and its own nested shape — an object " +
                    "property has no single scalar kind. Declare one or the other."));
            }

            if (hasValueKind && normalisedKind is not ("number" or "string" or "boolean"))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "CALC_FIELD_INVALID_VALUE_KIND",
                    propertyPath,
                    $"'{propertyPath}' declares valueKind '{property.ValueKind}'. Expected \"number\", " +
                    "\"string\" or \"boolean\", or omit it for a nested object (declare \"shape\" instead)."));
            }

            if (hasDefault && !hasValueKind)
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "CALC_FIELD_DEFAULT_WITHOUT_VALUE_KIND",
                    propertyPath,
                    $"'{propertyPath}' declares a default but no valueKind — validation can't parse the " +
                    "default without knowing its kind. Add \"valueKind\": \"number\" | \"string\" | \"boolean\"."));
            }
            else if (hasDefault && normalisedKind == "number" &&
                     !decimal.TryParse(property.Default!.Replace("£", "").Replace(",", "").Trim(),
                         NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "CALC_FIELD_DEFAULT_UNPARSEABLE",
                    propertyPath,
                    $"'{propertyPath}' declares valueKind \"number\" but its default '{property.Default}' is not a number."));
            }
            else if (hasDefault && normalisedKind == "boolean" &&
                     !bool.TryParse(property.Default!.Trim(), out _))
            {
                diagnostics.Add(new ServiceBlueprintDiagnostic(
                    "CALC_FIELD_DEFAULT_UNPARSEABLE",
                    propertyPath,
                    $"'{propertyPath}' declares valueKind \"boolean\" but its default '{property.Default}' is not true/false."));
            }

            if (hasNestedShape)
            {
                ValidateCalculationFieldShape(propertyPath, property.Shape!, diagnostics);
            }
        }
    }

    /// <summary>
    /// Every blueprint field key a registered support system's capability declares in its own
    /// <see cref="SupportSystems.SupportSystemCapabilityDescriptor.Outputs"/>, for every
    /// <c>support-system-call</c> action anywhere in this blueprint that references it —
    /// <see cref="ValidateDataDisplayBindings"/>'s "known field" set for stat-group/summary-list
    /// bindings. An action referencing an unregistered support system or capability contributes
    /// nothing here; that's <see cref="ValidateSupportSystemActions"/>'s own diagnostic to raise.
    /// </summary>
    private HashSet<string> GetSupportSystemOutputFieldKeys()
    {
        var outputFieldKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            foreach (var action in stage.Actions ?? [])
            {
                if (!string.Equals(action.Type, SupportSystems.SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal))
                {
                    continue;
                }

                var supportSystemKey = action.Parameters["supportSystemKey"]?.GetValue<string>();
                var capabilityKey = action.Parameters["capabilityKey"]?.GetValue<string>();
                if (supportSystemKey is null || capabilityKey is null)
                {
                    continue;
                }

                var capability = SupportSystems.SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey);
                if (capability is null)
                {
                    continue;
                }

                foreach (var output in capability.Outputs)
                {
                    outputFieldKeys.Add(output.Key);
                }
            }
        }

        return outputFieldKeys;
    }
}
