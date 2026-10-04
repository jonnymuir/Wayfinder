using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>Outcome of validating a service blueprint.</summary>
public sealed record ServiceBlueprintValidationOutcome(bool IsValid, IReadOnlyList<ServiceBlueprintDiagnostic> Diagnostics)
{
    public static ServiceBlueprintValidationOutcome Valid { get; } = new(true, Array.Empty<ServiceBlueprintDiagnostic>());
}
