using System.Text.Json.Serialization;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ServiceBlueprintSaveStatus>))]
public enum ServiceBlueprintSaveStatus { Saved, Invalid, Conflict }

/// <summary>
/// Outcome of a <see cref="ServiceBlueprintAuthoringService.SaveAsync"/> call — distinguishes a
/// successful save from a validation failure (<see cref="Diagnostics"/> from <see cref="ServiceBlueprintAuthoringService.Validate"/>)
/// and from an optimistic-concurrency conflict (<see cref="CurrentVersion"/> is what's actually
/// persisted now; the caller's <c>expectedVersion</c> was stale).
/// </summary>
public sealed record ServiceBlueprintSaveOutcome(
    ServiceBlueprintSaveStatus Status,
    IReadOnlyList<ServiceBlueprintDiagnostic> Diagnostics,
    int? CurrentVersion = null,
    int? NewVersion = null)
{
    public bool IsSaved => Status == ServiceBlueprintSaveStatus.Saved;

    public static ServiceBlueprintSaveOutcome Saved(int newVersion) =>
        new(ServiceBlueprintSaveStatus.Saved, Array.Empty<ServiceBlueprintDiagnostic>(), NewVersion: newVersion);

    public static ServiceBlueprintSaveOutcome Invalid(IReadOnlyList<ServiceBlueprintDiagnostic> diagnostics) =>
        new(ServiceBlueprintSaveStatus.Invalid, diagnostics);

    public static ServiceBlueprintSaveOutcome Conflict(int currentVersion) =>
        new(
            ServiceBlueprintSaveStatus.Conflict,
            [new ServiceBlueprintDiagnostic(
                "SAVE_VERSION_CONFLICT",
                "version",
                $"Blueprint has changed since it was loaded — current version is {currentVersion}, which didn't match the expected version. Reload and reapply your change.")],
            CurrentVersion: currentVersion);
}
