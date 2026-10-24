namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// Abstract base for all blueprint component types in the v2.0 schema.
/// Written with a "type" discriminator, registered in <see cref="ComponentTypeRegistry"/>.
/// </summary>
public abstract record Component
{
    /// <summary>
    /// Optional visibility expression evaluated against the blueprint's calculation scope
    /// (inputs, service inputs and calculated fields). When it evaluates to false the
    /// component renders hidden; the live-form runtime re-evaluates it as inputs change.
    /// </summary>
    public string? ShowWhen { get; init; }
}
