using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Sanitization;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// Proves <c>StageDefinition.Validations</c> — the declarative alternative to a host overriding
/// <c>ProcessManagerEngine.ValidateAdvance</c> with bespoke C# — is enforced server-side inside
/// <see cref="ProcessManagerEngine.Advance"/> itself. The gate field (<c>hasIssue</c>) is captured
/// on an earlier stage than the rule that reads it (<c>notes</c>), proving a rule can reference
/// any earlier-captured field with no extra wiring.
/// </summary>
public class ProcessManagerEngineStageValidationTests
{
    private const string DefinitionKey = "stage-validation-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "stage-validation-test",
          "displayName": "Stage Validation Test",
          "version": 1,
          "initialStage": "details",
          "requestPolicy": "single",
          "queues": [
            { "key": "citizen", "displayName": "Applicant", "actor": "citizen" }
          ],
          "calculations": {
            "fields": {
              "hasEvidence": { "expr": "matches(notes, '\\d')" }
            }
          },
          "stages": [
            {
              "stageKey": "details",
              "displayName": "Details",
              "queueKey": "citizen",
              "components": [
                { "type": "boolean", "fieldKey": "hasIssue", "label": "Reporting an issue", "default": "false" }
              ],
              "routes": [
                { "id": "details--continue", "target": "to-notes", "trigger": "continue", "label": "Continue" }
              ]
            },
            {
              "stageKey": "notes",
              "displayName": "Notes",
              "queueKey": "citizen",
              "components": [
                { "type": "text", "fieldKey": "notes", "label": "Notes", "default": "" }
              ],
              "validations": [
                {
                  "code": "evidence-required",
                  "when": "hasIssue",
                  "rule": "hasEvidence",
                  "field": "notes",
                  "message": "Include a concrete detail (e.g. a reference number) in your notes."
                }
              ],
              "routes": [
                { "id": "notes--continue", "target": "to-done", "trigger": "continue", "label": "Continue" }
              ]
            },
            {
              "stageKey": "done",
              "displayName": "Done",
              "queueKey": "citizen",
              "components": [
                { "type": "panel", "heading": "Done" }
              ]
            }
          ],
          "gateways": [
            {
              "key": "to-notes",
              "displayName": "Continue to notes",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-notes--continue", "target": "notes", "trigger": "continue" }
              ]
            },
            {
              "key": "to-done",
              "displayName": "Continue to done",
              "gatewayType": "Split",
              "queueKey": "citizen",
              "routes": [
                { "id": "to-done--continue", "target": "done", "trigger": "continue" }
              ]
            }
          ]
        }
        """;

    private static ProcessManagerEngine BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        return new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
    }

    /// <summary>Starts an instance and advances it to the "notes" stage with the given hasIssue value.</summary>
    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) ArriveAtNotes(bool hasIssue)
    {
        var engine = BuildEngine();
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);

        var atNotes = engine.Advance(
            started.InstanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", started.StateVersion, new Dictionary<string, object?> { ["hasIssue"] = hasIssue });

        Assert.Equal("Notes", atNotes.Render?.StateDisplayName);
        return (engine, started.InstanceId, atNotes.StateVersion);
    }

    [Fact]
    public void Advance_BlocksWhenGateAppliesAndRuleFails()
    {
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "no digits here" });

        Assert.Contains(result.Problems, p => p.FieldKey == "notes" && p.Code == "evidence-required");
        // Rejected before advancing — still on "notes", never saved past it.
        Assert.Equal("Notes", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_AllowsWhenGateAppliesAndRulePasses()
    {
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: true);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "see ref 42" });

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_SkipsRuleWhenGateDoesNotApply()
    {
        // hasIssue captured false on the EARLIER "details" stage — the rule on "notes" must read
        // it from there, and skip entirely, regardless of what "notes" itself contains.
        var (engine, instanceId, stateVersion) = ArriveAtNotes(hasIssue: false);

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "continue", stateVersion, new Dictionary<string, object?> { ["notes"] = "no digits here" });

        Assert.Empty(result.Problems);
        Assert.Equal("Done", result.Render?.StateDisplayName);
    }
}

/// <summary>
/// <see cref="ServiceBlueprintStageValidationRule.Actions"/> — scoping a stage validation rule
/// to specific route triggers, distinct from and complementary to
/// <see cref="ServiceBlueprintRouteDefinition.ShowWhen"/> (see
/// JugglingLicenceStageValidationTests' own "under-review" coverage for that). The two answer
/// different questions about the same kind of stage: ShowWhen decides which routes are even
/// *offered* (an approve/reject stage doesn't need this — both should always be visible); an
/// Actions-scoped rule decides which of the *always-offered* routes should be blocked with an
/// explanation until something holds — here, approving requires a completed checklist, but
/// rejecting never does, and both stay in AvailableActions throughout.
/// </summary>
public class ProcessManagerEngineStageValidationActionScopeTests
{
    private const string DefinitionKey = "stage-validation-action-scope-test";
    private const string TenantId = "tenant";
    private const string UserId = "user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string BlueprintJson = """
        {
          "definitionKey": "stage-validation-action-scope-test",
          "displayName": "Stage Validation Action Scope Test",
          "version": 1,
          "initialStage": "review",
          "requestPolicy": "single",
          "queues": [
            { "key": "caseworker", "displayName": "Caseworker", "actor": "caseworker" }
          ],
          "stages": [
            {
              "stageKey": "review",
              "displayName": "Review",
              "queueKey": "caseworker",
              "components": [
                { "type": "boolean", "fieldKey": "checklistComplete", "label": "Checklist complete", "default": "false" }
              ],
              "validations": [
                {
                  "code": "checklist-required-to-approve",
                  "rule": "checklistComplete",
                  "actions": ["approve"],
                  "message": "Complete the checklist before approving."
                }
              ],
              "routes": [
                { "id": "review--approve", "target": "to-approved", "trigger": "approve", "label": "Approve" },
                { "id": "review--reject", "target": "to-rejected", "trigger": "reject", "label": "Reject" }
              ]
            },
            {
              "stageKey": "approved",
              "displayName": "Approved",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Approved" } ]
            },
            {
              "stageKey": "rejected",
              "displayName": "Rejected",
              "queueKey": "caseworker",
              "components": [ { "type": "panel", "heading": "Rejected" } ]
            }
          ],
          "gateways": [
            {
              "key": "to-approved",
              "displayName": "Continue to approved",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-approved--approve", "target": "approved", "trigger": "approve" } ]
            },
            {
              "key": "to-rejected",
              "displayName": "Continue to rejected",
              "gatewayType": "Split",
              "queueKey": "caseworker",
              "routes": [ { "id": "to-rejected--reject", "target": "rejected", "trigger": "reject" } ]
            }
          ]
        }
        """;

    private static (ProcessManagerEngine Engine, string InstanceId, int StateVersion) BuildEngine()
    {
        var definition = JsonSerializer.Deserialize<ServiceBlueprint>(BlueprintJson, JsonOptions)!;
        var engine = new ProcessManagerEngine(
            NullLogger.Instance,
            new SingleDefinitionServiceBlueprintStore(definition),
            new PassthroughContentSanitizer());
        var started = engine.GetCurrent(DefinitionKey, TenantId, UserId);
        return (engine, started.InstanceId, started.StateVersion);
    }

    [Fact]
    public void BothRoutesAreAlwaysOffered_RegardlessOfChecklistState()
    {
        // The contrast with ShowWhen: an Actions-scoped rule never removes a route from
        // AvailableActions, only blocks-with-a-message when it's actually used.
        var (engine, instanceId, _) = BuildEngine();

        var current = engine.GetCurrent(DefinitionKey, TenantId, UserId, ActorProfile.UnrestrictedOwner, instanceId);

        var actionKeys = current.Render?.AvailableActions.Select(a => a.ActionKey).ToArray() ?? [];
        Assert.Contains("approve", actionKeys);
        Assert.Contains("reject", actionKeys);
    }

    [Fact]
    public void Advance_BlocksApproveWhenChecklistIncomplete()
    {
        var (engine, instanceId, stateVersion) = BuildEngine();

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "approve", stateVersion, null);

        Assert.Contains(result.Problems, p => p.Code == "checklist-required-to-approve");
    }

    [Fact]
    public void Advance_StillAllowsRejectWhenChecklistIncomplete()
    {
        // The whole point of scoping the rule to "approve": an unscoped rule would block this
        // too, even though nothing about rejecting depends on the checklist at all.
        var (engine, instanceId, stateVersion) = BuildEngine();

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner, "reject", stateVersion, null);

        Assert.Empty(result.Problems);
        Assert.Equal("Rejected", result.Render?.StateDisplayName);
    }

    [Fact]
    public void Advance_AllowsApproveWhenChecklistComplete()
    {
        var (engine, instanceId, stateVersion) = BuildEngine();

        var result = engine.Advance(
            instanceId, TenantId, UserId, ActorProfile.UnrestrictedOwner,
            "approve", stateVersion, new Dictionary<string, object?> { ["checklistComplete"] = true });

        Assert.Empty(result.Problems);
        Assert.Equal("Approved", result.Render?.StateDisplayName);
    }
}
