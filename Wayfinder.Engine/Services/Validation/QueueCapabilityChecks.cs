using Wayfinder.Engine.Abstractions;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Engine.Services.Validation;

/// <summary>
/// Checks against a host's <see cref="IQueueCapabilitiesProvider"/>, when one is registered: that
/// what it declares is itself valid, and that no stage uses a component its queue's host cannot
/// render. Without a provider there is nothing to check.
/// </summary>
internal sealed class QueueCapabilityChecks(IQueueCapabilitiesProvider? queueCapabilities)
{
    public IEnumerable<ServiceBlueprintDiagnostic> Diagnostics(ServiceBlueprint blueprint) =>
        queueCapabilities is null ? [] : DeclarationDiagnostics(queueCapabilities).Concat(UnsupportedComponentDiagnostics(queueCapabilities, blueprint));

    /// <summary>
    /// Cross-checks every discriminator a provider declares against <see cref="ComponentTypeRegistry"/>
    /// itself, catching a typo'd capability string (e.g. <c>"texts"</c> for <c>"text"</c>) at its
    /// source rather than as a downstream symptom (every component of the intended type reported as
    /// unsupported). Runs regardless of what the blueprint contains — a host's declared capabilities
    /// are static configuration, so a typo in a queue nothing currently authors for would otherwise go
    /// unnoticed indefinitely.
    /// </summary>
    private static IEnumerable<ServiceBlueprintDiagnostic> DeclarationDiagnostics(IQueueCapabilitiesProvider provider) =>
        from declared in provider.GetAllDeclaredCapabilities()
        from discriminator in declared.Value
        where ComponentTypeRegistry.Find(discriminator) is null
        select new ServiceBlueprintDiagnostic(
            "QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE",
            $"queues.{declared.Key}",
            $"Queue '{declared.Key}' declares support for component type '{discriminator}', but no such " +
            "type is registered in ComponentTypeRegistry — check for a typo. Call list_component_types " +
            "to see every valid discriminator.");

    /// <summary>
    /// Rejects any stage whose components exceed what its queue's host can actually render —
    /// otherwise a blueprint can be authored and saved with a component that silently renders as
    /// nothing at runtime. A queue with no declaration at all is unrestricted.
    /// </summary>
    private static IEnumerable<ServiceBlueprintDiagnostic> UnsupportedComponentDiagnostics(IQueueCapabilitiesProvider provider, ServiceBlueprint blueprint) =>
        from stage in blueprint.Stages
        let supportedTypes = provider.GetSupportedComponentTypes(stage.QueueKey)
        where supportedTypes is not null
        from entry in stage.Components.FlattenWithPaths($"stages.{stage.StageKey}.components")
        let discriminator = ComponentTypeRegistry.DiscriminatorFor(entry.Component)
        where !supportedTypes.Contains(discriminator, StringComparer.Ordinal)
        select Unsupported(stage, entry.Path, discriminator, supportedTypes);

    private static ServiceBlueprintDiagnostic Unsupported(StageDefinition stage, string path, string discriminator, IReadOnlyCollection<string> supportedTypes) =>
        new(
            "QUEUE_CAPABILITY_UNSUPPORTED_COMPONENT",
            path,
            $"State '{stage.StageKey}' uses component type '{discriminator}', which queue " +
            $"'{stage.QueueKey}''s host does not declare support for " +
            (supportedTypes.Count == 0
                ? "(it currently supports no component types at all). "
                : $"(it supports: {string.Join(", ", supportedTypes)}). ") +
            "Remove/replace this component, or extend that host's IQueueCapabilitiesProvider " +
            "declaration once it can actually render it. Call list_queue_capabilities to check " +
            "what a queue supports before drafting for it.");
}
