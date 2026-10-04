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
    /// The only <see cref="RequestPolicy"/> values <see cref="Services.ProcessManagerEngine"/>'s
    /// own instance-lookup switch recognises (<c>GetCurrent</c>'s <c>"multiple"</c>/<c>"prompt"</c>
    /// checks). An unrecognised value isn't rejected at runtime — it just falls straight through
    /// to <c>"single"</c>'s own fallthrough branch with no warning anywhere in the
    /// validate/load/execute path, so a typo (<c>"muliple"</c>) silently changes a blueprint's
    /// concurrency behaviour without ever surfacing as an error. This is the same "fails open,
    /// silently" shape already found and fixed once for <c>showWhen</c> — Warning severity, not
    /// Error, since it never crashes anything; it just isn't what the author almost certainly meant.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownRequestPolicies = ["single", "multiple", "prompt"];

    /// <summary>
    /// Validates that <see cref="RequestPolicy"/> is one of <see cref="KnownRequestPolicies"/>
    /// (case-insensitive). Returns at most one diagnostic; empty list means the declared policy is
    /// recognised.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateRequestPolicy()
    {
        if (KnownRequestPolicies.Any(known => string.Equals(known, RequestPolicy, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return
        [
            new ServiceBlueprintDiagnostic(
                "REQUEST_POLICY_UNKNOWN_VALUE",
                "requestPolicy",
                $"requestPolicy '{RequestPolicy}' isn't one of {string.Join(", ", KnownRequestPolicies)} — " +
                "it silently behaves exactly like 'single' at runtime, with no warning anywhere else. Fix the " +
                "value, or change it deliberately if 'single' really is what's intended.",
                ServiceBlueprintDiagnosticSeverity.Warning)
        ];
    }
}
