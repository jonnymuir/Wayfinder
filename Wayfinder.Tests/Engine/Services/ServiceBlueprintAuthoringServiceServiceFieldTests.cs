using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

/// <summary>
/// Static validation of <c>source: "service"</c> calculation fields: their optional
/// authoring-time <c>valueKind</c>/<c>default</c> aids, and the guarantee that an unverifiable
/// service field only ever produces a Warning and never stops the rest of the calculation /
/// <c>showWhen</c> / stage-rule pass from running (so a genuine mistake elsewhere is still caught
/// in the same call). See ServiceBlueprintCalculationField and
/// ServiceBlueprintAuthoringService.Validate.
/// </summary>
public class ServiceBlueprintAuthoringServiceServiceFieldTests
{
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

    private static readonly ServiceBlueprintAuthoringService Service = new(new UnusedStore());

    /// <param name="serviceField">The single <c>source: "service"</c> calc field under test.</param>
    /// <param name="acceptShowWhen">The review stage's "accept" route <c>showWhen</c> — references the service field.</param>
    /// <param name="extraRouteShowWhen">An optional second route <c>showWhen</c>, for planting a genuine mistake.</param>
    private static ServiceBlueprint Blueprint(
        ServiceBlueprintCalculationField serviceField,
        string acceptShowWhen = "flagCount = 0",
        string? extraRouteShowWhen = null) => new()
    {
        DefinitionKey = "service-field-test",
        DisplayName = "Test",
        InitialStage = "review",
        Calculations = new ServiceBlueprintCalculationSet
        {
            Fields = new Dictionary<string, ServiceBlueprintCalculationField> { ["flagCount"] = serviceField },
        },
        Stages =
        [
            new StageDefinition
            {
                StageKey = "review",
                DisplayName = "Review",
                QueueKey = "caseworker",
                Components = [new TextInputComponent { FieldKey = "notes", Label = "Notes", Default = "" }],
                Routes =
                [
                    new ServiceBlueprintRouteDefinition
                    {
                        Id = "review--accept", Target = "to-done", Trigger = "accept", ShowWhen = acceptShowWhen,
                    },
                    .. (extraRouteShowWhen is null
                        ? Array.Empty<ServiceBlueprintRouteDefinition>()
                        : new[]
                        {
                            new ServiceBlueprintRouteDefinition
                            {
                                Id = "review--other", Target = "to-done", Trigger = "other", ShowWhen = extraRouteShowWhen,
                            },
                        }),
                ],
            },
            new StageDefinition { StageKey = "done", DisplayName = "Done", QueueKey = "caseworker" },
        ],
        Gateways =
        [
            new ServiceBlueprintGatewayDefinition
            {
                Key = "to-done",
                DisplayName = "Continue to done",
                GatewayType = "Split",
                QueueKey = "caseworker",
                Routes =
                [
                    new ServiceBlueprintRouteDefinition { Id = "to-done--accept", Target = "done", Trigger = "accept" },
                    new ServiceBlueprintRouteDefinition { Id = "to-done--other", Target = "done", Trigger = "other" },
                ],
            },
        ],
    };

    [Fact]
    public void ServiceField_WithNumberValueKindAndDefault_ValidatesCleanWithNoWarning()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service", ValueKind = "number", Default = "0" }));

        outcome.IsValid.Should().BeTrue();
        outcome.Diagnostics.Should().NotContain(d => d.Code == "CALC_SERVICE_FIELD_UNVERIFIED");
        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("ROUTE_SHOW_WHEN_"));
    }

    [Fact]
    public void ServiceField_WithStringValueKind_NeedsNoDefaultAndProducesNoWarning()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service", ValueKind = "string" },
            acceptShowWhen: "flagCount = ''"));

        outcome.IsValid.Should().BeTrue();
        outcome.Diagnostics.Should().NotContain(d => d.Code == "CALC_SERVICE_FIELD_UNVERIFIED");
    }

    [Fact]
    public void ServiceField_WithNoValueKind_WarnsButDoesNotBlockOrError()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service" }));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "CALC_SERVICE_FIELD_UNVERIFIED" && d.Path == "calculations.fields.flagCount");
        // The showWhen that reads the unresolved field is reported as unverified, not an error.
        outcome.Diagnostics.Should().Contain(d => d.Code == "ROUTE_SHOW_WHEN_UNVERIFIED");
        outcome.Diagnostics.Should().NotContain(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error);
        outcome.IsValid.Should().BeTrue();
    }

    [Fact]
    public void UnresolvedServiceField_DoesNotHideAGenuineErrorElsewhereInTheSamePass()
    {
        // flagCount is unresolvable (warning + its own showWhen unverified), but the SECOND route's
        // showWhen references a name that genuinely exists nowhere — that must still be a blocking
        // error, not skipped by an early return.
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service" },
            extraRouteShowWhen: "definitelyNotAField"));

        outcome.Diagnostics.Should().Contain(d => d.Code == "CALC_SERVICE_FIELD_UNVERIFIED");
        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "ROUTE_SHOW_WHEN_EVAL_ERROR" &&
            d.Path == "stages.review.routes[1].showWhen" &&
            d.Message.Contains("definitelyNotAField"));
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void FieldThatReferencesAnUnresolvedServiceField_IsUnverifiedNotAnError()
    {
        // Regression: validation used to only downgrade the diagnostic for the unresolved
        // service field itself — a SECOND field whose own formula reads it (directly, or through
        // a dotted member access on it) failed evaluation for the same underlying reason, but got
        // reported as a genuine CALC_FIELD_ERROR instead of the same "unverified" Warning. That's
        // exactly as unverifiable as the root field, not a real authoring mistake.
        var blueprint = new ServiceBlueprint
        {
            DefinitionKey = "service-field-transitive-test",
            DisplayName = "Test",
            InitialStage = "review",
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField>
                {
                    ["member"] = new() { Source = "service" },
                    ["ageNextYear"] = new() { Expr = "member.age + 1" },
                },
            },
            Stages =
            [
                new StageDefinition
                {
                    StageKey = "review",
                    DisplayName = "Review",
                    QueueKey = "caseworker",
                    Components = [new TextInputComponent { FieldKey = "notes", Label = "Notes", Default = "" }],
                    Routes = [new ServiceBlueprintRouteDefinition { Id = "review--accept", Target = "to-done", Trigger = "accept" }],
                },
                new StageDefinition { StageKey = "done", DisplayName = "Done", QueueKey = "caseworker" },
            ],
            Gateways =
            [
                new ServiceBlueprintGatewayDefinition
                {
                    Key = "to-done",
                    DisplayName = "Continue to done",
                    GatewayType = "Split",
                    QueueKey = "caseworker",
                    Routes = [new ServiceBlueprintRouteDefinition { Id = "to-done--accept", Target = "done", Trigger = "accept" }],
                },
            ],
        };

        var outcome = Service.Validate(blueprint);

        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "CALC_SERVICE_FIELD_UNVERIFIED" && d.Path == "calculations.fields.member");
        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "CALC_FIELD_UNVERIFIED" && d.Path == "calculations.fields.ageNextYear");
        outcome.Diagnostics.Should().NotContain(d => d.Severity == ServiceBlueprintDiagnosticSeverity.Error);
        outcome.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ServiceField_WithUnknownValueKind_IsAnError()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service", ValueKind = "integer" }));

        outcome.Diagnostics.Should().Contain(d =>
            d.Code == "CALC_FIELD_INVALID_VALUE_KIND" && d.Path == "calculations.fields.flagCount");
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ServiceField_WithDefaultButNoValueKind_IsAnError()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service", Default = "0" }));

        outcome.Diagnostics.Should().Contain(d => d.Code == "CALC_FIELD_DEFAULT_WITHOUT_VALUE_KIND");
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ServiceField_WithNumberValueKindButNonNumericDefault_IsAnError()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Source = "service", ValueKind = "number", Default = "nope" }));

        outcome.Diagnostics.Should().Contain(d => d.Code == "CALC_FIELD_DEFAULT_UNPARSEABLE");
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValueKind_OnANonServiceField_IsAnError()
    {
        var outcome = Service.Validate(Blueprint(
            new ServiceBlueprintCalculationField { Expr = "1", ValueKind = "number" },
            acceptShowWhen: "flagCount = 0"));

        outcome.Diagnostics.Should().Contain(d => d.Code == "CALC_FIELD_VALUE_KIND_WITHOUT_SERVICE");
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void MockServiceInputs_StillOverrideADeclaredDefault()
    {
        // default: "0" would make "flagCount = 0" true; a mock of 3 makes it false. Either way it
        // evaluates cleanly with no warning — this just proves the mock still wins.
        var outcome = Service.Validate(
            Blueprint(new ServiceBlueprintCalculationField { Source = "service", ValueKind = "number", Default = "0" }),
            new Dictionary<string, object?> { ["flagCount"] = 3m });

        outcome.IsValid.Should().BeTrue();
        outcome.Diagnostics.Should().NotContain(d => d.Code == "CALC_SERVICE_FIELD_UNVERIFIED");
    }
}
