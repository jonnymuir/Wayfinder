using System.Reflection;
using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// Reflects <see cref="Component"/>'s <c>[JsonDerivedType]</c> attribute list ONCE into
/// static lookups — the single source of truth for which component "type" discriminator
/// strings actually exist, avoiding a second hand-maintained list that can drift. This class
/// used to carry a cautionary example of exactly that drift: a hardcoded switch elsewhere in
/// the runtime still listing "tel" despite <c>TelComponent</c> having no <c>[JsonDerivedType]</c>
/// entry and therefore never actually being deserializable. Fixed by deleting the dead type and
/// its orphaned switch arms rather than finishing it — see git history if a real telephone input
/// type is wanted later; it would need a renderer case (<c>GovUkFields.cs</c> never had one
/// either) as well as the discriminator entry to actually work.
///
/// This also doubles as Wayfinder's own honest, published declaration of what its stock rendering
/// pipeline supports — referenced directly (not re-derived or guessed) by any host wanting to
/// assert "I render the full built-in catalog" for one of its queues.
/// </summary>
public static class ComponentTypeCatalog
{
    private static readonly IReadOnlyDictionary<string, Type> DiscriminatorToTypeMap = BuildDiscriminatorToType();

    private static readonly IReadOnlyDictionary<Type, string> TypeToDiscriminatorMap =
        DiscriminatorToTypeMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    /// <summary>Every valid "type" discriminator string, e.g. "text", "summary-list", "chart".</summary>
    public static IReadOnlyList<string> AllDiscriminators { get; } =
        DiscriminatorToTypeMap.Keys.OrderBy(d => d, StringComparer.Ordinal).ToList();

    /// <summary>The discriminator string for a component instance, e.g. "text" for TextInputComponent.</summary>
    public static string DiscriminatorFor(Component component) =>
        TypeToDiscriminatorMap.TryGetValue(component.GetType(), out var discriminator)
            ? discriminator
            : throw new InvalidOperationException(
                $"{component.GetType().Name} has no [JsonDerivedType] discriminator on Component.");

    private static IReadOnlyDictionary<string, Type> BuildDiscriminatorToType() =>
        typeof(Component)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .ToDictionary(a => (string)a.TypeDiscriminator!, a => a.DerivedType, StringComparer.Ordinal);
}
