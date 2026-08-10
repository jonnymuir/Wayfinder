using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

/// <summary>
/// Proves <see cref="ServiceBlueprintAuthoringService.Validate"/> statically checks every
/// <c>StageDefinition.Validations</c> <c>when</c>/<c>rule</c> expression before a blueprint can be
/// saved — the same treatment already applied to every component's <c>showWhen</c> — so a broken
/// or non-boolean rule is caught at authoring time, not discovered the first time a real
/// submission silently fails to advance.
/// </summary>
public class ServiceBlueprintAuthoringServiceStageValidationTests
{
    // Validate() never touches the store — see ServiceBlueprintAuthoringServiceCapabilityTests.
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

    private static ServiceBlueprint BlueprintWithRule(string? when, string rule) => new()
    {
        DefinitionKey = "stage-validation-diagnostics-test",
        DisplayName = "Test",
        InitialStage = "only",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "only",
                DisplayName = "only",
                QueueKey = "citizen",
                Components = [new TextInputComponent { FieldKey = "notes", Label = "Notes", Default = "" }],
                Validations = [new ServiceBlueprintStageValidationRule("evidence-required", rule, "Message", When: when)],
            },
        ],
    };

    [Fact]
    public void WellFormedRule_ProducesNoDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithRule(when: "true", rule: "notes = ''"));

        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("STAGE_VALIDATION_"));
    }

    [Fact]
    public void RuleReferencingUnknownName_ProducesRuleEvalErrorDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithRule(when: null, rule: "nosuchthing"));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "STAGE_VALIDATION_RULE_EVAL_ERROR" &&
            d.Path == "stages.only.validations[0].rule" &&
            d.Message.Contains("nosuchthing"));
        outcome.IsValid.Should().BeFalse();
    }

    [Fact]
    public void WhenReferencingUnknownName_ProducesWhenEvalErrorDiagnostic()
    {
        var outcome = Service.Validate(BlueprintWithRule(when: "nosuchthing", rule: "true"));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "STAGE_VALIDATION_WHEN_EVAL_ERROR" &&
            d.Path == "stages.only.validations[0].when" &&
            d.Message.Contains("nosuchthing"));
    }

    [Fact]
    public void RuleThatEvaluatesToANumber_ProducesRuleEvalErrorDiagnostic()
    {
        // Evaluates cleanly (no CalculationException) but isn't a boolean — ProcessManagerEngine
        // would silently treat it as "not exactly true" and fail the rule on every submission, a
        // much harder bug to spot than a diagnostic caught here.
        var outcome = Service.Validate(BlueprintWithRule(when: null, rule: "1 + 1"));

        outcome.Diagnostics.Should().ContainSingle(d =>
            d.Code == "STAGE_VALIDATION_RULE_EVAL_ERROR" &&
            d.Path == "stages.only.validations[0].rule" &&
            d.Message.Contains("not true/false"));
    }

    [Fact]
    public void RuleReferencingACalculatedField_ResolvesAgainstTheCalculationScope()
    {
        var blueprint = BlueprintWithRule(when: "true", rule: "hasEvidence") with
        {
            Calculations = new ServiceBlueprintCalculationSet
            {
                Fields = new Dictionary<string, ServiceBlueprintCalculationField>
                {
                    ["hasEvidence"] = new() { Expr = "matches(notes, '\\d')" },
                },
            },
        };

        var outcome = Service.Validate(blueprint);

        outcome.Diagnostics.Should().NotContain(d => d.Code.StartsWith("STAGE_VALIDATION_"));
    }
}
