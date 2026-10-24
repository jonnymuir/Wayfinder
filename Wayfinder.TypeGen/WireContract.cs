namespace Wayfinder.TypeGen;

/// <summary>How a C# type is written on one wire: the shape the generated TypeScript must match.</summary>
public abstract class WireContract
{
    /// <summary>The element type when <paramref name="type"/> is written as a map (string keys) or a list; null for an object.</summary>
    public abstract (WireKind Kind, Type? Element) Classify(Type type);

    /// <summary>The properties an object type is written with, in order.</summary>
    public abstract IReadOnlyList<WireMember> Members(Type type);

    /// <summary>For an abstract base written as one of several discriminated types; null otherwise.</summary>
    public abstract WirePolymorphism? Polymorphism(Type type);

    /// <summary>The text an enum value is written as.</summary>
    public abstract string EnumValue(Type enumType, object value);
}

public enum WireKind
{
    Structure,
    Map,
    List,
}

/// <param name="Optional">A reader can find it missing.</param>
/// <param name="TypeScript">The TypeScript type when the wire shape is not the property's own type.</param>
public sealed record WireMember(string Name, Type Type, bool Optional, string? TypeScript = null);

public sealed record WirePolymorphism(string DiscriminatorProperty, IReadOnlyList<(Type Type, string Discriminator)> DerivedTypes);
