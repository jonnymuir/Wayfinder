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
/// <remarks>
/// This registry is process-wide, not per-tenant: every blueprint definition served by a single
/// process draws from the same support-system catalog, deliberately mirroring blueprint
/// definitions themselves being a shared catalog rather than a per-tenant one (see
/// <c>Wayfinder.Umbraco</c>'s blueprint store). A multi-tenant host that needs two tenants to see
/// genuinely different support-system catalogs in one process needs a different, explicitly
/// tenant-keyed mechanism — this registry's freeze-on-first-read design is intentionally
/// incompatible with per-tenant reconfiguration, not an oversight.
/// </remarks>
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

            foreach (var input in capability.Inputs)
            {
                ValidateWireSafeKey(input.Key, "input", capability.Key, descriptor.Key);
            }

            foreach (var output in capability.Outputs)
            {
                ValidateWireSafeKey(output.Key, "output", capability.Key, descriptor.Key);
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

    /// <summary>
    /// Catches a real mistake the hard way (found live, in a genuinely running editor): unlike a
    /// component's own <c>ComponentPropertyDescriptor.Key</c> values — always a real CLR property
    /// name passed via <see langword="nameof"/>, so <c>PropertyNameJsonConverter</c> lowercasing
    /// its first letter for the wire is a deliberate, harmless PascalCase→camelCase translation —
    /// a support-system capability's <c>Inputs</c>/<c>Outputs</c> keys are arbitrary,
    /// author-chosen identifiers with no backing CLR property at all. The *exact same* converter
    /// still runs (both reuse <see cref="Components.ComponentPropertyDescriptor"/>), so a
    /// PascalCase key here — the natural instinct, since <see langword="nameof"/>-style PascalCase
    /// is the convention everywhere else in this toolkit — silently becomes a different string
    /// over the wire than what the engine matches internally: the editor's live-fetched catalog
    /// and a blueprint's own <c>params.inputs</c>/<c>params.outputs</c> mapping keys stop
    /// agreeing, and every reference to it fails validation with no clue why. Requiring the key to
    /// already be wire-safe (first character not uppercase) makes the JSON conversion a no-op,
    /// closing the gap at registration time instead of leaving it to be found the same painful way
    /// this one was.
    /// </summary>
    private static void ValidateWireSafeKey(string key, string kind, string capabilityKey, string supportSystemKey)
    {
        if (!string.IsNullOrEmpty(key) && char.IsUpper(key[0]))
        {
            throw new ArgumentException(
                $"Capability '{capabilityKey}' on support system '{supportSystemKey}' declares {kind} key " +
                $"'{key}', which starts with an uppercase letter. {kind[0].ToString().ToUpperInvariant()}{kind[1..]} " +
                $"keys are serialized over the wire through the same converter as a component property's " +
                $"CLR name (lowercasing the first letter) — a capability's own key has no such CLR property " +
                $"behind it, so this would silently become '{char.ToLowerInvariant(key[0])}{key[1..]}' wherever " +
                "a blueprint or the editor reads it back, no longer matching what this descriptor itself uses " +
                $"internally. Use '{char.ToLowerInvariant(key[0])}{key[1..]}' instead.");
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
