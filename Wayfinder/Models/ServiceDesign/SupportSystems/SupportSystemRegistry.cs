namespace Wayfinder.Models.ServiceDesign.SupportSystems;

/// <summary>
/// The single source of truth for which support systems a host has registered and what each one
/// can do — mirrors <see cref="Components.ComponentTypeRegistry"/>'s own registration pattern
/// (frozen-on-first-read, <see cref="Register"/> throws after that), minus the JSON-polymorphism
/// half of that registry's job: a <see cref="SupportSystemDescriptor"/> is never itself part of
/// <see cref="Components.Component"/>'s polymorphic hierarchy, it's purely a lookup a
/// <c>support-system-call</c> action resolves against by <see cref="SupportSystemDescriptor.Key"/>.
/// A host registers its own support systems once, at startup — see
/// docs/guides/support-systems.md for the full picture.
/// </summary>
public static class SupportSystemRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, SupportSystemDescriptor> ByKey = new(StringComparer.Ordinal);
    private static bool _frozen;

    /// <summary>
    /// Test-only escape hatch: unfreezes the registry and clears every registration, so a test
    /// can register its own fixture support system without permanently polluting the shared,
    /// process-wide registry for every other test that runs afterwards in the same process.
    /// Never call this from real host code.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            ByKey.Clear();
            _frozen = false;
        }
    }

    /// <summary>
    /// Registers a support system. Call during host startup, before any blueprint referencing
    /// it is read, validated, or run.
    /// </summary>
    /// <exception cref="ArgumentException">The descriptor is structurally invalid — see the message for which rule.</exception>
    /// <exception cref="InvalidOperationException">The registry is already frozen, or the key is already taken.</exception>
    public static void Register(SupportSystemDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);

        lock (Lock)
        {
            if (_frozen)
            {
                throw new InvalidOperationException(
                    $"SupportSystemRegistry is frozen — a support system has already been read, so " +
                    $"'{descriptor.Key}' can't be registered now. Register every support system at host " +
                    "startup, before the first blueprint referencing it is loaded.");
            }

            if (ByKey.ContainsKey(descriptor.Key))
            {
                throw new InvalidOperationException($"A support system is already registered for key '{descriptor.Key}'.");
            }

            ByKey[descriptor.Key] = descriptor;
        }
    }

    private static void ValidateDescriptor(SupportSystemDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Key))
        {
            throw new ArgumentException("A support system must have a non-empty Key.", nameof(descriptor));
        }

        var capabilityKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in descriptor.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.Key))
            {
                throw new ArgumentException(
                    $"Support system '{descriptor.Key}' has a capability with no Key.", nameof(descriptor));
            }

            if (!capabilityKeys.Add(capability.Key))
            {
                throw new ArgumentException(
                    $"Support system '{descriptor.Key}' registers capability '{capability.Key}' more than once.",
                    nameof(descriptor));
            }

            if (capability.SupportedCompletionModes.Count == 0)
            {
                throw new ArgumentException(
                    $"Capability '{capability.Key}' on support system '{descriptor.Key}' must declare at least " +
                    $"one {nameof(SupportSystemCompletionMode)} — otherwise its outcome could never be delivered.",
                    nameof(descriptor));
            }

            if (capability.Outcomes.Count == 0)
            {
                throw new ArgumentException(
                    $"Capability '{capability.Key}' on support system '{descriptor.Key}' must declare at least " +
                    "one possible outcome.",
                    nameof(descriptor));
            }

            var outcomeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var outcome in capability.Outcomes)
            {
                if (!outcomeKeys.Add(outcome.Key))
                {
                    throw new ArgumentException(
                        $"Capability '{capability.Key}' on support system '{descriptor.Key}' declares outcome " +
                        $"'{outcome.Key}' more than once.",
                        nameof(descriptor));
                }
            }
        }
    }

    /// <summary>Every registered support system, ordered by key. Freezes the registry.</summary>
    public static IReadOnlyList<SupportSystemDescriptor> All
    {
        get
        {
            lock (Lock)
            {
                _frozen = true;
                return ByKey.Values.OrderBy(d => d.Key, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Looks up a support system by its key. Freezes the registry.</summary>
    public static SupportSystemDescriptor? Find(string key)
    {
        lock (Lock)
        {
            _frozen = true;
            return ByKey.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Looks up one capability of one support system directly — the lookup a
    /// <c>support-system-call</c> action resolves against. Freezes the registry.
    /// </summary>
    public static SupportSystemCapabilityDescriptor? FindCapability(string supportSystemKey, string capabilityKey) =>
        Find(supportSystemKey)?.Capabilities.FirstOrDefault(c => c.Key == capabilityKey);
}
