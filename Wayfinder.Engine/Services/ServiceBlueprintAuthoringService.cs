using Wayfinder.Models.ServiceDesign;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services.Validation;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Transport-agnostic service blueprint authoring surface: list/read/validate/save/simulate
/// definitions against a host-supplied <see cref="IServiceBlueprintSourceStore"/>. Reusable by
/// any front door (MCP tools, a CLI, a host's own code) — no MCP dependency here.
/// </summary>
public sealed class ServiceBlueprintAuthoringService(
    IServiceBlueprintSourceStore store,
    IEnumerable<IServiceBlueprintStructuralValidator>? structuralValidators = null,
    IQueueCapabilitiesProvider? queueCapabilities = null)
{
    private readonly ServiceBlueprintValidator _validator = new(structuralValidators?.ToArray() ?? [], queueCapabilities);

    public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
        store.ListAsync(ct);

    public Task<ServiceBlueprint?> ReadAsync(string definitionKey, CancellationToken ct = default) =>
        store.LoadAsync(definitionKey, ct);

    public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
        store.DeleteAsync(definitionKey, ct);

    /// <summary>
    /// Every queue this host has declared render capabilities for, per
    /// <see cref="IQueueCapabilitiesProvider.GetAllDeclaredCapabilities"/> — empty if no
    /// provider is registered.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetQueueCapabilities() =>
        queueCapabilities?.GetAllDeclaredCapabilities() ?? new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Validates the blueprint, collecting every diagnostic rather than stopping at the first. A
    /// declared <c>source: "service"</c> field not covered by <paramref name="mockServiceInputs"/>
    /// can't be verified statically — it's reported as a
    /// <see cref="ServiceBlueprintDiagnosticSeverity.Warning"/>, not an error, and the expressions
    /// that depend on it are reported as unverified. Pass real values via
    /// <paramref name="mockServiceInputs"/>, or use <c>Simulate</c>, to verify those fully.
    /// </summary>
    public ServiceBlueprintValidationOutcome Validate(
        ServiceBlueprint blueprint,
        IReadOnlyDictionary<string, object?>? mockServiceInputs = null) =>
        _validator.Validate(blueprint, mockServiceInputs);

    /// <summary>
    /// Validates, then saves only if <paramref name="expectedVersion"/> still matches what's
    /// currently persisted (see <see cref="IServiceBlueprintSourceStore.SaveAsync"/>). Pass <c>0</c> for
    /// a blueprint you expect doesn't exist yet.
    /// </summary>
    public async Task<ServiceBlueprintSaveOutcome> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default)
    {
        var validation = Validate(blueprint);
        if (!validation.IsValid)
        {
            return ServiceBlueprintSaveOutcome.Invalid(validation.Diagnostics);
        }

        var result = await store.SaveAsync(blueprint, expectedVersion, ct);
        return result.Saved
            ? ServiceBlueprintSaveOutcome.Saved(result.CurrentVersion)
            : ServiceBlueprintSaveOutcome.Conflict(result.CurrentVersion);
    }
}
