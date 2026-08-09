using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// The single source of truth for which component "type" discriminator strings exist, what
/// each one is (<see cref="ComponentDescriptor"/>), and how to make <see cref="Component"/>'s
/// JSON polymorphism recognise it — replacing the old, compile-time-fixed
/// <c>[JsonDerivedType]</c> attribute list as the *only* way a discriminator gets registered.
/// Built-ins (<see cref="BuiltInComponentDescriptors"/>) register at static-init time; a host
/// or toolkit extension registers its own the same way, via <see cref="Register{TComponent}"/>,
/// at startup. See docs/guides/extending-the-component-catalog.md for the full guide.
///
/// The registry is a startup-time, one-shot thing: it freezes the first time anything actually
/// reads it (<see cref="All"/>, <see cref="Find"/>, <see cref="DescriptorFor"/>, or the first
/// real (de)serialization via <see cref="JsonTypeInfoResolver"/>) and <see cref="Register{TComponent}"/>
/// throws after that — the alternative (silently allowing late registration) risks an
/// already-cached <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> for
/// <see cref="Component"/> that doesn't include a type registered too late, which would be a far
/// more confusing failure than a clear startup-time exception.
/// </summary>
public static class ComponentTypeRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, ComponentDescriptor> ByDiscriminator = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, ComponentDescriptor> ByClrType = new();
    private static bool _frozen;

    static ComponentTypeRegistry()
    {
        foreach (var descriptor in BuiltInComponentDescriptors.All)
        {
            RegisterCore(descriptor);
        }
    }

    /// <summary>
    /// Test-only escape hatch: unfreezes the registry and drops every custom registration back
    /// to just the built-ins, so a test can register its own fixture type without permanently
    /// polluting the shared, process-wide registry for every other test that runs afterwards in
    /// the same process. Never call this from real host code — a real app registers its custom
    /// types once, at startup, and never needs (or should want) to reset.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            ByDiscriminator.Clear();
            ByClrType.Clear();
            _frozen = false;
            foreach (var descriptor in BuiltInComponentDescriptors.All)
            {
                RegisterCore(descriptor);
            }
        }
    }

    /// <summary>
    /// Registers a component type — built-in or a toolkit extension's own. Call during host
    /// startup, before any <see cref="ServiceBlueprint"/> is (de)serialized. Pair this with a
    /// <c>GovUkComponentRenderer.RegisterComponent</c>/<c>RegisterField</c> override
    /// (<c>Wayfinder.Rendering.GovUk</c>) to also supply rendering — "what it is" and "how it
    /// renders" are two separate, complementary registrations.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="descriptor"/>.ClrType doesn't match <typeparamref name="TComponent"/>.</exception>
    /// <exception cref="InvalidOperationException">The registry is already frozen, or the discriminator is already taken.</exception>
    public static void Register<TComponent>(ComponentDescriptor descriptor) where TComponent : Component
    {
        if (descriptor.ClrType != typeof(TComponent))
        {
            throw new ArgumentException(
                $"{nameof(ComponentDescriptor.ClrType)} ({descriptor.ClrType.Name}) must match the registered " +
                $"type parameter ({typeof(TComponent).Name}).",
                nameof(descriptor));
        }

        lock (Lock)
        {
            if (_frozen)
            {
                throw new InvalidOperationException(
                    $"ComponentTypeRegistry is frozen — a component has already been read/(de)serialized, so " +
                    $"'{descriptor.Discriminator}' can't be registered now. Register every custom component " +
                    "type at host startup, before the first ServiceBlueprint is loaded.");
            }

            RegisterCore(descriptor);
        }
    }

    private static void RegisterCore(ComponentDescriptor descriptor)
    {
        if (ByDiscriminator.ContainsKey(descriptor.Discriminator))
        {
            throw new InvalidOperationException(
                $"A component type is already registered for discriminator '{descriptor.Discriminator}'.");
        }

        ByDiscriminator[descriptor.Discriminator] = descriptor;
        ByClrType[descriptor.ClrType] = descriptor;
    }

    /// <summary>Every registered descriptor, built-in and custom, ordered by discriminator. Freezes the registry.</summary>
    public static IReadOnlyList<ComponentDescriptor> All
    {
        get
        {
            lock (Lock)
            {
                _frozen = true;
                return ByDiscriminator.Values.OrderBy(d => d.Discriminator, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Every registered discriminator string, e.g. "text", "summary-list", "chart". Freezes the registry.</summary>
    public static IReadOnlyList<string> AllDiscriminators => All.Select(d => d.Discriminator).ToArray();

    /// <summary>Looks up a descriptor by its discriminator string. Freezes the registry.</summary>
    public static ComponentDescriptor? Find(string discriminator)
    {
        lock (Lock)
        {
            _frozen = true;
            return ByDiscriminator.GetValueOrDefault(discriminator);
        }
    }

    /// <summary>The descriptor for a live component instance. Freezes the registry.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="component"/>'s CLR type was never registered.</exception>
    public static ComponentDescriptor DescriptorFor(Component component)
    {
        lock (Lock)
        {
            _frozen = true;
            return ByClrType.TryGetValue(component.GetType(), out var descriptor)
                ? descriptor
                : throw new InvalidOperationException(
                    $"{component.GetType().Name} has no registered ComponentDescriptor — every Component-derived " +
                    $"type must be registered via {nameof(ComponentTypeRegistry)}.{nameof(Register)}.");
        }
    }

    /// <summary>The discriminator string for a component instance, e.g. "text" for TextInputComponent.</summary>
    public static string DiscriminatorFor(Component component) => DescriptorFor(component).Discriminator;

    /// <summary>
    /// The discriminator string for a registered CLR type, e.g. <c>DiscriminatorFor&lt;TextInputComponent&gt;()</c>
    /// → <c>"text"</c> — without needing a live instance. Lets call sites that declare a
    /// per-queue/per-host allow-list of component types (e.g. <c>IQueueCapabilitiesProvider</c>
    /// declarations) reference the real registered type instead of a bare string literal, so a
    /// typo or a stale entry after a rename breaks the build instead of silently drifting — the
    /// exact failure mode <c>ValidateQueueCapabilityDeclarations</c> exists to catch at runtime;
    /// this catches the same class of mistake earlier, at compile time, for anyone who chooses to
    /// use it. Freezes the registry.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TComponent"/> was never registered.</exception>
    public static string DiscriminatorFor<TComponent>() where TComponent : Component
    {
        lock (Lock)
        {
            _frozen = true;
            return ByClrType.TryGetValue(typeof(TComponent), out var descriptor)
                ? descriptor.Discriminator
                : throw new InvalidOperationException(
                    $"{typeof(TComponent).Name} has no registered ComponentDescriptor — every Component-derived " +
                    $"type must be registered via {nameof(ComponentTypeRegistry)}.{nameof(Register)}.");
        }
    }

    /// <summary>
    /// An <see cref="IJsonTypeInfoResolver"/> that makes <see cref="Component"/>'s polymorphic
    /// (de)serialization recognise every currently-registered discriminator — built-ins and any
    /// custom types registered before first use. Wire into
    /// <c>JsonSerializerOptions.TypeInfoResolver</c> wherever a <see cref="ServiceBlueprint"/> is
    /// read or written (see <see cref="ServiceBlueprintJson"/>, the one shared place this
    /// already happens). Uses System.Text.Json's own contract-customisation API
    /// (<c>DefaultJsonTypeInfoResolver</c> + a <c>Modifiers</c> delegate) rather than a
    /// hand-rolled <c>JsonConverter&lt;Component&gt;</c> — it reuses STJ's own battle-tested
    /// polymorphic engine (nested discriminators, unknown-type error messages, etc.) instead of
    /// reimplementing it for 27+ types. Deliberately does NOT remove the static
    /// <c>[JsonDerivedType]</c> list already on <see cref="Component"/> — the built-ins keep
    /// working exactly as before; this is additive, listing every *registered* type (built-in
    /// and custom) on top.
    /// </summary>
    public static IJsonTypeInfoResolver CreateJsonTypeInfoResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(Component))
            {
                return;
            }

            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "type",
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };

            foreach (var descriptor in All)
            {
                typeInfo.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(descriptor.ClrType, descriptor.Discriminator));
            }
        });

        return resolver;
    }
}
