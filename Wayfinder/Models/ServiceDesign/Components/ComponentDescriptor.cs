using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// Where a component type sits in the catalog — matches the categorisation already used in
/// docs/guides/reference-service-blueprint-contract.md's own prose catalog exactly, now made
/// machine-readable instead of re-derived by hand wherever it's needed.
/// </summary>
public enum ComponentCategory
{
    /// <summary>Declares a <c>fieldKey</c>, participates in the calculation scope.</summary>
    Input,
    /// <summary>No <c>fieldKey</c>, purely presentational.</summary>
    Content,
    /// <summary>Contains other components — see <see cref="ComponentDescriptor.Containment"/>.</summary>
    Container,
    /// <summary>Binds to a calculated value or captured input for display.</summary>
    DataDisplay,
    /// <summary>Used at gateways, not authored as an ordinary stage component (e.g. <c>waiting</c>).</summary>
    FlowControl,
}

/// <summary>
/// The kind of value a <see cref="ComponentPropertyDescriptor"/> holds — deliberately the same
/// small vocabulary as <c>AuthoredParameterDefinition.valueKind</c> (the editor's own proven
/// schema shape for action parameters, <c>Wayfinder.Editor.Client/src/service-blueprint-editor/types.ts</c>),
/// so a component's property schema and an action's parameter schema are the same shape a host
/// or editor UI only has to understand once.
/// </summary>
public enum ComponentPropertyValueKind
{
    String,
    Number,
    Integer,
    Boolean,
    StringArray,
    /// <summary>A nested object — see <see cref="ComponentPropertyDescriptor.Properties"/>.</summary>
    Object,
    /// <summary>A list of a single element shape — see <see cref="ComponentPropertyDescriptor.Items"/>.</summary>
    Array,
}

/// <summary>
/// Describes one property of a component type — enough for a JSON Schema, a generic editor
/// form field, and basic structural validation (required/type/allowed-values/constraints).
/// Recursive (<see cref="Properties"/>/<see cref="Items"/>) so it can describe a nested record
/// (e.g. <c>ChartBand</c>, <c>StatItemDefinition</c>) or a list of one, the same way
/// <c>AuthoredParameterDefinition</c> already does for action parameters.
/// </summary>
public sealed record ComponentPropertyDescriptor
{
    /// <summary>The JSON property name, e.g. <c>"minLength"</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label for editor UI, e.g. "Minimum length".</summary>
    public required string Title { get; init; }

    /// <summary>Longer help text — editor tooltip / AI-agent-readable prose.</summary>
    public string? Description { get; init; }

    public required ComponentPropertyValueKind ValueKind { get; init; }

    /// <summary>Semantic hint for the value's shape, e.g. <c>"email"</c>, <c>"date"</c>, <c>"color"</c>.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// Explicit editor widget hint (e.g. <c>"textarea"</c>, <c>"select"</c>, <c>"toggle"</c>) —
    /// same vocabulary as <c>AuthoredParameterDefinition.editor</c>. Null means "infer from
    /// ValueKind/AllowedValues", the same fallback the action-parameter editor already uses.
    /// </summary>
    public string? Editor { get; init; }

    /// <summary>Closed set of legal string values, if any (renders as a select/radio group).</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    public bool Required { get; init; }

    public object? DefaultValue { get; init; }

    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }

    /// <summary>Nested property schema when <see cref="ValueKind"/> is <see cref="ComponentPropertyValueKind.Object"/>.</summary>
    public IReadOnlyList<ComponentPropertyDescriptor>? Properties { get; init; }

    /// <summary>Element schema when <see cref="ValueKind"/> is <see cref="ComponentPropertyValueKind.Array"/>.</summary>
    public ComponentPropertyDescriptor? Items { get; init; }
}

/// <summary>The shape of a container component's child slot(s).</summary>
public enum ContainmentKind
{
    /// <summary>A leaf — no children (most component types).</summary>
    None,
    /// <summary>A single flat <c>IReadOnlyList&lt;Component&gt;</c> property, e.g. <c>FieldsetComponent.Children</c>.</summary>
    ChildList,
    /// <summary>A list of named sections, each with its own children, e.g. <c>AccordionComponent.Sections</c>.</summary>
    NamedSections,
    /// <summary>
    /// An <c>IReadOnlyDictionary&lt;string, IReadOnlyList&lt;Component&gt;&gt;</c> keyed by a value
    /// that should be a subset of another property on the same component, e.g.
    /// <c>RadiosComponent.ConditionalChildren</c> keyed against <c>Options</c>.
    /// </summary>
    KeyedChildren,
}

/// <summary>
/// Describes how to find a component's children, generalising the only three shapes that
/// actually exist across the built-in catalog (verified) into one thing a generic tree-walker
/// and a generic editor UI can both drive, instead of a hand-maintained switch per shape that
/// silently misses one (see <c>ComponentExtensions.Flatten</c>'s history — it never descended
/// into <c>SummaryListComponent.Children</c>, structurally identical to <c>FieldsetComponent
/// .Children</c>, simply because nobody added a case for it).
/// </summary>
public sealed record ComponentContainment
{
    public static readonly ComponentContainment None = new() { Kind = ContainmentKind.None };

    public required ContainmentKind Kind { get; init; }

    /// <summary>
    /// The CLR property (on the component's own record) holding the children
    /// (<see cref="ContainmentKind.ChildList"/>/<see cref="ContainmentKind.KeyedChildren"/>) or
    /// sections (<see cref="ContainmentKind.NamedSections"/>).
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// For <see cref="ContainmentKind.NamedSections"/> only: the property on each section
    /// record holding its own children (e.g. <c>AccordionSection.Children</c>).
    /// </summary>
    public string? SectionChildrenPropertyName { get; init; }

    /// <summary>
    /// For <see cref="ContainmentKind.KeyedChildren"/> only: the property on the *same*
    /// component whose values a valid key should be a subset of (e.g. <c>"Options"</c>) — lets
    /// the generic validator catch a <c>ConditionalChildren</c> key that doesn't match any
    /// declared option, a check that doesn't exist anywhere today.
    /// </summary>
    public string? KeySourceProperty { get; init; }

    public static ComponentContainment ChildList(string propertyName) =>
        new() { Kind = ContainmentKind.ChildList, PropertyName = propertyName };

    public static ComponentContainment NamedSections(string propertyName, string sectionChildrenPropertyName) =>
        new()
        {
            Kind = ContainmentKind.NamedSections,
            PropertyName = propertyName,
            SectionChildrenPropertyName = sectionChildrenPropertyName,
        };

    public static ComponentContainment KeyedChildren(string propertyName, string keySourceProperty) =>
        new() { Kind = ContainmentKind.KeyedChildren, PropertyName = propertyName, KeySourceProperty = keySourceProperty };
}

/// <summary>
/// The full description of a component type — identity, where it renders (<see cref="Category"/>),
/// what properties it has (for a JSON Schema, generic editor form, and structural validation),
/// and how (if at all) it contains other components. The single thing every one of the catalog's
/// previously-hand-duplicated enumerations (JSON deserialization, tree-walking, editor display
/// labels, TS types, capability strings, prose docs) should now derive from or be checked
/// against — see <see cref="ComponentTypeRegistry"/>.
/// </summary>
public sealed record ComponentDescriptor
{
    /// <summary>The <c>"type"</c> discriminator, e.g. <c>"text"</c>, <c>"fieldset"</c>.</summary>
    public required string Discriminator { get; init; }

    /// <summary>Human-readable name for editor UI, e.g. "Text input".</summary>
    public required string DisplayName { get; init; }

    public required ComponentCategory Category { get; init; }

    /// <summary>Longer help text — editor tooltip / AI-agent-readable prose.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The CLR type backing this discriminator — must derive from <see cref="Component"/>.
    /// <see cref="Type"/> itself isn't meaningfully JSON-serializable (reflecting over its own
    /// huge surface), so this serializes as just its <see cref="Type.Name"/> (e.g.
    /// <c>"TextInputComponent"</c>) via <see cref="ClrTypeNameJsonConverter"/> — a converter
    /// rather than <c>[JsonIgnore]</c>: System.Text.Json treats a <see langword="required"/>
    /// member that's also ignored as a contradiction (it can never be satisfied during
    /// deserialization) and throws when building a contract for the type. Hit for real: the
    /// <c>list_component_types</c> MCP tool's auto-generated output schema — built by
    /// <c>Microsoft.Extensions.AI</c> — crashed the whole host at startup on exactly this before
    /// it became a converter instead.
    /// </summary>
    [JsonConverter(typeof(ClrTypeNameJsonConverter))]
    public required Type ClrType { get; init; }

    /// <summary>True for types deriving from <see cref="InputComponent"/> — declares a <c>fieldKey</c> and participates in the calculation scope.</summary>
    public bool IsInput { get; init; }

    public IReadOnlyList<ComponentPropertyDescriptor> Properties { get; init; } = Array.Empty<ComponentPropertyDescriptor>();

    public ComponentContainment Containment { get; init; } = ComponentContainment.None;
}

/// <summary>
/// Writes a <see cref="ComponentDescriptor.ClrType"/> as just <see cref="Type.Name"/> — a
/// <see cref="ComponentDescriptor"/> is only ever hand-constructed in code (see
/// <see cref="BuiltInComponentDescriptors"/>), never deserialized from JSON, so
/// <see cref="Read"/> is unreachable in practice and throws rather than pretend to support it.
/// </summary>
internal sealed class ClrTypeNameJsonConverter : JsonConverter<Type>
{
    public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            $"{nameof(ComponentDescriptor)}.{nameof(ComponentDescriptor.ClrType)} is not deserializable — " +
            "descriptors are constructed in code, never read from JSON.");

    public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Name);
}
