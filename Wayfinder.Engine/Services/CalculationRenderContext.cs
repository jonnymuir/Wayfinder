using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Engine.Services;

/// <summary>Evaluated calculation stage for one render pass.</summary>
internal sealed record CalculationRenderContext(
    ServiceBlueprintCalculationSet Set,
    IReadOnlyDictionary<string, object?> Scope,
    CalculationResult Result,
    IReadOnlyDictionary<string, object?> DisplayValues);
