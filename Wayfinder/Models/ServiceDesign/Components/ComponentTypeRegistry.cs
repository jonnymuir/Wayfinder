namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>
/// The single source of truth for which component "type" discriminator strings exist, what
/// each one is (<see cref="ComponentDescriptor"/>). <see cref="ServiceBlueprintYaml"/> reads a
/// component's <c>type</c> through it, so registering is the only way a discriminator exists.
/// Built-ins (<see cref="BuiltInComponentDescriptors"/>) register at static-init time; a host
/// or toolkit extension registers its own the same way, via <see cref="Register{TComponent}"/>,
/// at startup. See docs/guides/extending-the-component-catalog.md for the full guide.
///
/// The registry is a startup-time, one-shot thing: it freezes the first time anything actually
/// reads it (<see cref="All"/>, <see cref="Find"/>, <see cref="DescriptorFor"/>, or the first
/// blueprint read or written) and <see cref="Register{TComponent}"/> throws after that, so a type
/// registered too late fails clearly at startup rather than half-working.
/// </summary>
/// <remarks>
/// Process-wide, not per-tenant, by design — mirrors <see cref="SupportSystems.SupportSystemRegistry"/>'s
/// own remarks on the same point. Every blueprint served by a single process draws from the same
/// component-type catalog, consistent with blueprint definitions themselves being a shared,
/// process-wide catalog rather than a per-tenant one.
/// </remarks>
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

    /// <summary>The descriptor registered for a component CLR type, or null. Freezes the registry.</summary>
    public static ComponentDescriptor? FindByClrType(Type clrType)
    {
        lock (Lock)
        {
            _frozen = true;
            return ByClrType.GetValueOrDefault(clrType);
        }
    }
}
