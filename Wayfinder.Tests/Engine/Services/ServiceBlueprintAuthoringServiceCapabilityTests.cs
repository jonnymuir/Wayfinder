using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

/// <summary>
/// Proves the motivating complaint behind this whole exercise is actually fixed: a typo'd
/// capability string in an <see cref="IQueueCapabilitiesProvider"/> declaration (e.g.
/// <see cref="Wayfinder.ReferenceApp.Services.ReferenceActors"/>'s own real declarations, which
/// this reuses the exact shape of) is now caught directly, not just as a downstream symptom.
/// </summary>
public class ServiceBlueprintAuthoringServiceCapabilityTests
{
    // Validate() never touches the store — a throwing stub proves that, and avoids pulling in an
    // unrelated in-memory implementation just to satisfy the constructor.
    private sealed class UnusedStore : IServiceBlueprintSourceStore
    {
        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static ServiceBlueprint MinimalBlueprint() => new()
    {
        DefinitionKey = "capability-typo-test",
        DisplayName = "Capability typo test",
        InitialStage = "only",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "only",
                DisplayName = "Only stage",
                QueueKey = "citizen",
                Components = [new TextInputComponent { FieldKey = "name", Label = "Name" }],
            },
        ],
    };

    [Fact]
    public void Validate_CapabilityProviderDeclaresUnknownDiscriminator_ReturnsUnknownComponentTypeDiagnostic()
    {
        var capabilities = new StaticQueueCapabilitiesProvider(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                // "texts" — a typo for "text" — is exactly the failure mode that started this
                // work: a queue capability string with zero connection to the real catalog.
                ["citizen"] = ["texts", "panel"],
            });
        var service = new ServiceBlueprintAuthoringService(new UnusedStore(), queueCapabilities: capabilities);

        var outcome = service.Validate(MinimalBlueprint());

        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE" &&
            d.Path == "queues.citizen" &&
            d.Message.Contains("texts"));
    }

    [Fact]
    public void Validate_CapabilityProviderDeclaresOnlyRealDiscriminators_ReturnsNoUnknownComponentTypeDiagnostic()
    {
        var capabilities = new StaticQueueCapabilitiesProvider(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["citizen"] = ["text", "panel"],
            });
        var service = new ServiceBlueprintAuthoringService(new UnusedStore(), queueCapabilities: capabilities);

        var outcome = service.Validate(MinimalBlueprint());

        outcome.Diagnostics.Should().NotContain(d => d.Code == "QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE");
    }

    [Fact]
    public void Validate_CapabilityDeclarationUnknownEvenForAQueueNoStageUses_IsStillCaught()
    {
        // The declaration check is independent of blueprint content — a typo in a queue nothing
        // currently authors for would otherwise stay hidden indefinitely.
        var capabilities = new StaticQueueCapabilitiesProvider(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["caseworker"] = ["summry-list"],
            });
        var service = new ServiceBlueprintAuthoringService(new UnusedStore(), queueCapabilities: capabilities);

        var outcome = service.Validate(MinimalBlueprint());

        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE" && d.Path == "queues.caseworker");
    }

    [Fact]
    public void Validate_ComponentPropertyMissingRequiredField_ReturnsComponentPropertyDiagnostic()
    {
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "component-property-test",
            DisplayName = "Component property test",
            InitialStage = "only",
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "only",
                    DisplayName = "Only stage",
                    QueueKey = "citizen",
                    Components = [new TextInputComponent { FieldKey = "", Label = "Name" }],
                },
            ],
        };
        var service = new ServiceBlueprintAuthoringService(new UnusedStore());

        var outcome = service.Validate(blueprint);

        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "COMPONENT_PROPERTY_REQUIRED" && d.Path == "stages.only.components[0].fieldKey");
        outcome.IsValid.Should().BeFalse();
    }
}
