using System.Globalization;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign.Components;
using SupportSystems = Wayfinder.Models.ServiceDesign.SupportSystems;
using BulkData = Wayfinder.Models.ServiceDesign.BulkData;

namespace Wayfinder.Models.ServiceDesign;

public partial record ServiceBlueprint
{
    /// <summary>
    /// Validates every <c>support-system-call</c> action against the registered
    /// <see cref="SupportSystems.SupportSystemRegistry"/>: that <c>supportSystemKey</c>/
    /// <c>capabilityKey</c> are present and actually registered, that every input the capability
    /// requires is bound in the action's own <c>params.inputs</c> mapping (and that mapping names
    /// only real declared inputs — catches a typo'd capability input key), that a bound input's
    /// blueprint field key actually exists somewhere in this blueprint, and that the carrying
    /// stage's own outgoing route triggers are all outcomes the capability can actually resolve
    /// to — a route whose trigger isn't one of <see cref="SupportSystems.SupportSystemCapabilityDescriptor.Outcomes"/>
    /// can never fire, since <c>ResolveSupportSystemOutcome</c> (<c>Wayfinder.Engine</c>) only
    /// ever delivers a declared outcome key. See docs/guides/support-systems.md.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateSupportSystemActions()
    {
        var diagnostics = new List<ServiceBlueprintDiagnostic>();
        var inputFieldKeys = Stages
            .SelectMany(s => s.Components.GetSubmittableInputs())
            .Select(c => c.FieldKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stage in Stages)
        {
            var actionIndex = 0;
            foreach (var action in stage.Actions ?? [])
            {
                var path = $"stages.{stage.StageKey}.actions[{actionIndex}]";
                actionIndex++;

                if (!string.Equals(action.Type, SupportSystems.SupportSystemActionTypes.SupportSystemCall, StringComparison.Ordinal))
                {
                    continue;
                }

                var supportSystemKey = action.Parameters.GetString("supportSystemKey");
                var capabilityKey = action.Parameters.GetString("capabilityKey");
                if (string.IsNullOrWhiteSpace(supportSystemKey) || string.IsNullOrWhiteSpace(capabilityKey))
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_MISSING_KEYS",
                        $"{path}.params",
                        "A support-system-call action must set both params.supportSystemKey and " +
                        "params.capabilityKey."));
                    continue;
                }

                var supportSystem = SupportSystems.SupportSystemRegistry.Find(supportSystemKey);
                if (supportSystem is null)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_UNKNOWN_SUPPORT_SYSTEM",
                        $"{path}.params.supportSystemKey",
                        $"'{supportSystemKey}' is not a registered support system — call " +
                        "list_support_systems to see what's available."));
                    continue;
                }

                var capability = supportSystem.Capabilities.FirstOrDefault(c => c.Key == capabilityKey);
                if (capability is null)
                {
                    diagnostics.Add(new ServiceBlueprintDiagnostic(
                        "SUPPORT_SYSTEM_ACTION_UNKNOWN_CAPABILITY",
                        $"{path}.params.capabilityKey",
                        $"'{capabilityKey}' is not a capability of support system '{supportSystemKey}'."));
                    continue;
                }

                diagnostics.AddRange(ValidateInputs(capability, action, path, inputFieldKeys));

                var declaredOutcomeKeys = capability.Outcomes.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
                diagnostics.AddRange(SupportSystems.SupportCallFailurePolicy.Validate(action.Parameters, path, declaredOutcomeKeys));
                foreach (var route in stage.Routes ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(route.Trigger) && !declaredOutcomeKeys.Contains(route.Trigger))
                    {
                        diagnostics.Add(new ServiceBlueprintDiagnostic(
                            "SUPPORT_SYSTEM_ACTION_ROUTE_TRIGGER_UNKNOWN_OUTCOME",
                            $"stages.{stage.StageKey}.routes",
                            $"Route trigger '{route.Trigger}' on stage '{stage.StageKey}' is not one of capability " +
                            $"'{capabilityKey}''s declared outcomes ({string.Join(", ", declaredOutcomeKeys)}) — it " +
                            "can never fire, since resolving this action only ever delivers a declared outcome key."));
                    }
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Checks an action's inputs against its capability: every required input is given, every given input is
    /// declared, and an input bound to a field names a field this blueprint captures.
    /// </summary>
    private static List<ServiceBlueprintDiagnostic> ValidateInputs(
        SupportSystems.SupportSystemCapabilityDescriptor capability, ActionDefinition action, string path, HashSet<string> inputFieldKeys)
    {
        var mapping = action.Parameters.GetMap("inputs");
        var given = mapping?.Where(kvp => !string.IsNullOrWhiteSpace(mapping.GetString(kvp.Key))).Select(kvp => kvp.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var problems = capability.Inputs
            .Where(input => input.Required && !given.Contains(input.Key))
            .Select(input => new ServiceBlueprintDiagnostic(
                "SUPPORT_SYSTEM_ACTION_MISSING_REQUIRED_INPUT",
                $"{path}.params.inputs",
                $"Capability '{capability.Key}' requires input '{input.Key}', which this action's params.inputs doesn't give."))
            .ToList();

        var check = new InputCheck(capability, path, inputFieldKeys);
        foreach (var key in mapping?.Select(kvp => kvp.Key) ?? [])
        {
            if (MappedInputProblem(check, key, mapping!.GetString(key)) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with one input the action gives, or null. A fixed input is a value the blueprint writes, not a
    /// field, so it is not looked up.
    /// </summary>
    private static ServiceBlueprintDiagnostic? MappedInputProblem(InputCheck check, string key, string? value)
    {
        var (capability, path, inputFieldKeys) = check;
        var input = capability.Inputs.FirstOrDefault(i => i.Key == key);
        if (input is null)
        {
            return new ServiceBlueprintDiagnostic(
                "SUPPORT_SYSTEM_ACTION_UNKNOWN_INPUT",
                $"{path}.params.inputs.{key}",
                $"'{key}' is not a declared input of capability '{capability.Key}' — call list_support_systems to see what it accepts.");
        }

        var boundToKnownField = !string.IsNullOrWhiteSpace(value) && inputFieldKeys.Contains(value);
        return input.Format == SupportSystems.SupportSystemInputFormats.Fixed || boundToKnownField
            ? null
            : new ServiceBlueprintDiagnostic(
                "SUPPORT_SYSTEM_ACTION_INPUT_UNKNOWN_FIELD",
                $"{path}.params.inputs.{key}",
                $"Input '{key}' is bound to field '{value}', which is not a captured input field anywhere in this blueprint.");
    }

    /// <summary>What every input of one action is checked against.</summary>
    private sealed record InputCheck(SupportSystems.SupportSystemCapabilityDescriptor Capability, string Path, HashSet<string> InputFieldKeys);
}
