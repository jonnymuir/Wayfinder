using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.Services.Calculations;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;

namespace Wayfinder.Engine.Mcp;

/// <summary>
/// MCP tool wrappers over <see cref="ServiceBlueprintAuthoringService"/>. Calls it directly,
/// in-process — this runs inside the same app as the live engine, so a save is visible
/// immediately, no restart.
/// </summary>
[McpServerToolType]
public static class ServiceBlueprintAuthoringTools
{
    // ServiceBlueprint.Stages[].Components is a [JsonPolymorphic] Component
    // hierarchy. The MCP SDK's own tool-argument binding doesn't set
    // AllowOutOfOrderMetadataProperties (github.com/modelcontextprotocol/csharp-sdk#795 —
    // no supported hook to configure it), so it fails whenever a component's "type"
    // discriminator isn't the first JSON property, which a model-constructed payload can't
    // be relied on to guarantee. Tools that take a blueprint as input accept it as a JSON
    // string and deserialize it themselves with ServiceBlueprintJson.ReadOptions — the same
    // shared options every other store/tool in the engine uses. Tools that only return a
    // ServiceBlueprint (read_service_blueprint) are unaffected — serialization already writes
    // the discriminator first — so those stay typed.

    [McpServerTool(Name = "list_service_blueprints")]
    [Description("List every service blueprint available in the connected host's store, with key and display name.")]
    public static Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListServiceBlueprints(
        ServiceBlueprintAuthoringService service, CancellationToken ct) =>
        service.ListAsync(ct);

    [McpServerTool(Name = "read_service_blueprint")]
    [Description("Read a service blueprint's full JSON by its definitionKey.")]
    public static Task<ServiceBlueprint?> ReadServiceBlueprint(
        ServiceBlueprintAuthoringService service,
        [Description("The blueprint's definitionKey, as returned by list_service_blueprints.")] string definitionKey,
        CancellationToken ct) =>
        service.ReadAsync(definitionKey, ct);

    private const string CalculationsShapeReminder =
        "A `calculations` block is a sibling of `stages`/`gateways`/`queues`, not an action or a " +
        "component property: { \"tables\": {}, \"fields\": { \"<fieldName>\": { \"expr\": \"<expression>\" } }, " +
        "\"series\": {} } — `fields` is a JSON OBJECT keyed by field name, each value an object with an " +
        "`expr` string (NOT a list of {name, expression} pairs). Only `stat-group`, `chart`, and " +
        "`summary-list` components render a calculated value on screen (binding by field/series name) — " +
        "`text`/`number`/etc. are INPUT components and never display one, however they're labelled, and a " +
        "`stat-group` with an empty `items` list renders nothing. Every input component's own `fieldKey` " +
        "is already automatically in the calculation scope — never redeclare it as a calculations.fields " +
        "entry with `\"source\": \"service\"`; that marker is only for a value an external system supplies " +
        "(e.g. a lookup), and doing this to your own captured input makes it permanently unresolvable. A " +
        "gateway needs a real, unique `key` (a keyless one can never be a route target), and every route's " +
        "`target` must actually match an existing gateway/stage key — an empty target is fine mid-edit but " +
        "must be wired up before you consider the blueprint finished. A `StageDefinition` may also carry " +
        "its own `validations`: [{ \"code\", \"when\"?, \"rule\", \"field\"?, \"message\" }] — cross-field " +
        "business rules checked before that stage can advance (the declarative alternative to a host " +
        "writing custom validation code), evaluated against the same blueprint-wide scope, so `rule` may " +
        "reference a field captured on an earlier stage. `when`/`rule` are both plain calculation-language " +
        "expressions and must evaluate to a boolean. " +
        "Read service-blueprint-docs://calculation-language before writing or editing a calculations block; it " +
        "has the full grammar and a worked example, including a \"Stage validations\" section for this.";

    [McpServerTool(Name = "list_queue_capabilities")]
    [Description(
        "List every queue this host has explicitly declared render capabilities for, " +
        "and which Component \"type\" discriminators (e.g. \"text\", \"summary-list\", " +
        "\"panel\") are supported for each. A queue key NOT present in this result is " +
        "unrestricted from this toolkit's point of view — not a declared concern of this host " +
        "(e.g. served by a different downstream app). Check this before drafting a stage for a " +
        "queue you haven't authored for before, rather than finding out from validate_service_blueprint's " +
        "QUEUE_CAPABILITY_UNSUPPORTED_COMPONENT diagnostic after the fact.")]
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ListQueueCapabilities(
        ServiceBlueprintAuthoringService service) =>
        service.GetQueueCapabilities();

    [McpServerTool(Name = "list_component_types")]
    [Description(
        "List every registered Component \"type\" discriminator this toolkit (built-in and any " +
        "toolkit extension's own) actually knows how to deserialize — the live source of truth " +
        "behind every \"type\" value used in a stage's components array. Each entry has " +
        "discriminator, displayName, category (Input/Content/Container/DataDisplay/FlowControl), " +
        "isInput (participates in the calculation scope), properties (key/title/valueKind/" +
        "required/allowedValues/format/pattern/minimum/maximum/minLength/maxLength, recursively " +
        "via properties/items for nested shapes like a chart's bands), and containment (none, or " +
        "how it holds child components — see the containment.kind/propertyName fields). Call this " +
        "before authoring a component you haven't used before, or before declaring a queue's " +
        "capabilities, to avoid a typo'd \"type\" string that validate_service_blueprint would " +
        "otherwise only catch indirectly. See also service-blueprint-docs://extending-the-component-catalog " +
        "for how to register a genuinely new component type.")]
    public static IReadOnlyList<ComponentDescriptor> ListComponentTypes() => ComponentTypeRegistry.All;

    [McpServerTool(Name = "list_support_systems")]
    [Description(
        "List every support system this host has registered — the external/downstream systems a " +
        "blueprint's stages/routes can call out to via a support-system-call action (Nielsen Norman " +
        "Group's \"support processes\" layer, the third lane alongside a citizen- and a caseworker-" +
        "facing queue). Each entry has key, displayName, and capabilities: [{ key, displayName, " +
        "inputs (same ComponentPropertyDescriptor shape as list_component_types' own properties — " +
        "an input tagged format \"field-ref\" must be bound to a blueprint field's fieldKey), " +
        "outputs (field keys this capability's resolution writes directly into instance state once " +
        "it resolves — a summary-list/stat-group elsewhere in the blueprint may legitimately bind to " +
        "one of these even though no stage ever captures it as an input), supportedCompletionModes " +
        "(Poll and/or Webhook — which mechanism(s) the engine will use to learn this capability's " +
        "outcome), and outcomes: [{ key, displayName }], the closed vocabulary a stage's outgoing " +
        "route triggers must be drawn from after this capability resolves }. Call this before " +
        "authoring a support-system-call action to avoid a typo'd supportSystemKey/capabilityKey, " +
        "an input that isn't bound to a real field, or an outgoing route trigger that doesn't match " +
        "a declared outcome. See also " +
        "service-blueprint-docs://support-systems for the full picture.")]
    public static IReadOnlyList<SupportSystemDescriptor> ListSupportSystems() => SupportSystemRegistry.All;

    [McpServerTool(Name = "validate_service_blueprint")]
    [Description(
        "Validate a service blueprint JSON — checks that every stage route targets a gateway " +
        "(never another stage directly), that any calculations block evaluates cleanly, that " +
        "every stage's own `validations` when/rule expressions evaluate cleanly and to a real " +
        "boolean (STAGE_VALIDATION_WHEN_EVAL_ERROR/_RULE_EVAL_ERROR) and that any `field` they " +
        "name is a real fieldKey on that same stage (STAGE_VALIDATION_UNKNOWN_FIELD), that " +
        "every stat-group/chart/summary-list component's bound field or series actually exists, " +
        "and that every component's own properties satisfy its registered type's requirements " +
        "(COMPONENT_PROPERTY_REQUIRED/_INVALID_VALUE/_PATTERN_MISMATCH/_TOO_SHORT/_TOO_LONG/" +
        "_TOO_SMALL/_TOO_LARGE, and COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH for a " +
        "ConditionalChildren key that doesn't match a declared option — call list_component_types " +
        "to see what's required per type). " +
        "Does not save. Returns { isValid, diagnostics }, each diagnostic { code, path, message, severity } " +
        "— severity \"Warning\" (e.g. an unverifiable service-sourced field) does not block isValid. " +
        "When the host declares queue render capabilities, also checks that every component in a " +
        "stage is actually supported by that stage's queue (QUEUE_CAPABILITY_UNSUPPORTED_COMPONENT " +
        "— call list_queue_capabilities first to check what a queue supports). Also checks every " +
        "support-system-call action's params against the registered support system/capability " +
        "(SUPPORT_SYSTEM_ACTION_MISSING_KEYS/_UNKNOWN_SUPPORT_SYSTEM/_UNKNOWN_CAPABILITY/" +
        "_MISSING_REQUIRED_INPUT/_UNKNOWN_INPUT/_INPUT_UNKNOWN_FIELD, and " +
        "_ROUTE_TRIGGER_UNKNOWN_OUTCOME when the carrying stage's own route triggers aren't among " +
        "the capability's declared outcomes — call list_support_systems first); a summary-list/" +
        "stat-group bound to a field only a support-system-call action's capability declares as an " +
        "output (not a captured input or calculations.fields entry) is recognised as valid, not " +
        "flagged DATA_DISPLAY_UNKNOWN_FIELD. " +
        CalculationsShapeReminder + " " +
        "See also service-blueprint-docs://authoring-guide for the full contract shape.")]
    public static ServiceBlueprintValidationOutcome ValidateServiceBlueprint(
        ServiceBlueprintAuthoringService service,
        [Description("The full ServiceBlueprint JSON to validate.")] string blueprintJson)
    {
        if (!TryDeserialize(blueprintJson, out var blueprint, out var diagnostic))
        {
            return new ServiceBlueprintValidationOutcome(false, [diagnostic!]);
        }
        return service.Validate(blueprint);
    }

    [McpServerTool(Name = "save_service_blueprint")]
    [Description(
        "Validate and save a service blueprint JSON to the connected host's live store — the " +
        "change is visible to the running app immediately, no restart. Invalid definitions " +
        "are rejected and NOT saved — check { status, errors } in the response (status: " +
        "\"Saved\", \"Invalid\", or \"Conflict\"). A Conflict means the blueprint's `version` " +
        "field is stale — someone else (a human in the editor, or another agent) saved a newer " +
        "version; re-read_service_blueprint to get the current version and reapply your change on top of " +
        "it before saving again. For a brand-new definitionKey that has never been saved, set " +
        "`version` to 0, not 1 — a non-existent blueprint's current version is 0, and copying " +
        "`\"version\": 1` from an existing seed you read as a style reference (that's its " +
        "*current* saved version, not a starting value) will Conflict on your very first save. " +
        "This is the only way to persist a blueprint change the running " +
        "app will honor; editing seed/source files directly (e.g. workflow-seeds/*.json) has no " +
        "effect on the live app. " + CalculationsShapeReminder + " " +
        "See also service-blueprint-docs://authoring-guide for the full contract shape.")]
    public static Task<ServiceBlueprintSaveOutcome> SaveServiceBlueprint(
        ServiceBlueprintAuthoringService service,
        [Description("The full ServiceBlueprint JSON to save, including the `version` you read it at.")] string blueprintJson,
        CancellationToken ct)
    {
        if (!TryDeserialize(blueprintJson, out var blueprint, out var diagnostic))
        {
            return Task.FromResult(ServiceBlueprintSaveOutcome.Invalid([diagnostic!]));
        }
        return service.SaveAsync(blueprint, blueprint.Version, ct);
    }

    [McpServerTool(Name = "simulate_service_blueprint")]
    [Description(
        "Dry-run a sequence of actions through a service blueprint with no persistence — " +
        "returns { trace, calculations }: trace is the stage trace (one entry per step: " +
        "response stage, available actions, problems) exactly as the real runtime would " +
        "report to a client; calculations is the raw calculated field/series values per step " +
        "(parallel to trace, null for steps with no calculations block), so you can check the " +
        "maths directly instead of parsing rendered UI text. A definition with a " +
        "source: \"service\" calculation field (e.g. money-modeller's \"member\") needs " +
        "mockServiceInputsJson supplying it, or those fields — and anything calculated from " +
        "them — are simply unresolved, the same as against a host with no data for them. Use " +
        "this to check a definition actually behaves as intended before saving it. The trace " +
        "follows a single cursor: if a Split's business-side branch routes to both a Join and " +
        "its own separate terminal stage, only one branch is followed and the other's actions " +
        "go unverified — route a reviewer/business action only into the Join, matching " +
        "payment-demo/information-request's convention, rather than giving it a parallel terminal.")]
    public static ServiceBlueprintSimulationResult SimulateServiceBlueprint(
        ServiceBlueprintAuthoringService service,
        [Description("The full ServiceBlueprint JSON to simulate.")] string blueprintJson,
        [Description(
            "JSON array of steps to advance through in order, e.g. " +
            "[{\"action\":\"submit\",\"fieldValues\":{\"name\":\"Ada\"}}].")]
        string stepsJson,
        [Description(
            "Optional JSON object of mock values for source: \"service\" calculation fields, e.g. " +
            "{\"member\":{\"age\":47,\"active\":true}}. Omit if the definition has none.")]
        string? mockServiceInputsJson = null)
    {
        if (!TryDeserialize(blueprintJson, out var blueprint, out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic!.Message);
        }
        var steps = JsonSerializer.Deserialize<List<ProcessManagerSimulationStep>>(stepsJson, ServiceBlueprintJson.ReadOptions)
            ?? [];
        var mockServiceInputs = string.IsNullOrWhiteSpace(mockServiceInputsJson)
            ? null
            : CalculationScopeJson.ToScopeValues(mockServiceInputsJson);
        return service.Simulate(blueprint, steps, mockServiceInputs);
    }

    // System.Text.Json throws on any malformed blueprintJson (wrong types, truncated JSON,
    // etc.) — without this guard, that exception bubbles out of the MCP tool call unhandled
    // instead of the structured { isValid/status, diagnostics } shape these tools document,
    // which the MCP SDK then surfaces as an opaque tool-call error rather than something an
    // agent can act on.
    private static bool TryDeserialize(
        string blueprintJson, out ServiceBlueprint blueprint, out ServiceBlueprintDiagnostic? diagnostic)
    {
        try
        {
            blueprint = JsonSerializer.Deserialize<ServiceBlueprint>(blueprintJson, ServiceBlueprintJson.ReadOptions)
                ?? throw new JsonException("blueprintJson did not deserialize to a ServiceBlueprint.");
            diagnostic = null;
            return true;
        }
        catch (JsonException ex)
        {
            blueprint = null!;
            diagnostic = new ServiceBlueprintDiagnostic(
                "INVALID_JSON", ex.Path ?? "$", $"blueprintJson is not a valid ServiceBlueprint: {ex.Message}");
            return false;
        }
    }
}
