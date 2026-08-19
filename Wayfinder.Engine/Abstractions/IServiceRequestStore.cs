using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// The toolkit's extension point for blueprint *instance* storage — mirrors
/// <see cref="IServiceBlueprintSourceStore"/>'s role for definitions. <see cref="Services.ProcessManagerEngine"/>
/// ships a default in-memory implementation, but a host that needs instances to survive a
/// process restart, or to expire on a schedule unrelated to the process lifetime (e.g. tied to a
/// browser session), implements this against its own persistence instead.
/// </summary>
public interface IServiceRequestStore
{
    /// <summary>Looks up a single instance by id.</summary>
    bool TryGet(string instanceId, out ServiceRequest instance);

    /// <summary>Upserts an instance.</summary>
    void Save(ServiceRequest instance);

    /// <summary>Removes an instance, if present. Returns whether one was removed.</summary>
    bool Remove(string instanceId);

    /// <summary>Removes every stored instance.</summary>
    void Clear();

    /// <summary>Returns every stored instance. Order is not guaranteed.</summary>
    IEnumerable<ServiceRequest> GetAll();

    /// <summary>
    /// Atomically saves <paramref name="instance"/> iff no instance is currently stored for its
    /// <see cref="ServiceRequest.InstanceId"/> and <paramref name="expectedStateVersion"/> is 0
    /// (a first-ever save), or a stored instance's own <see cref="ServiceRequest.StateVersion"/>
    /// equals <paramref name="expectedStateVersion"/>. Returns <see langword="false"/>, performing
    /// no save, if that check fails — the caller lost a race against a concurrent writer, or is
    /// working from stale data. This is the toolkit's sole compare-and-swap primitive: unlike
    /// <c>Advance</c>'s own pre-check against an expected version (a read-then-branch, not itself
    /// atomic against a second concurrent caller), this method's own check-and-write must happen as
    /// one indivisible operation against the store.
    ///
    /// The default implementation here is a plain check-then-<see cref="Save"/> — provided purely
    /// so an existing host-authored <see cref="IServiceRequestStore"/> keeps compiling after this
    /// method was added, NOT because it is safe to rely on. It is not atomic. Any store backing
    /// real concurrent pickup (including <c>InMemoryServiceRequestStore</c>, which overrides this
    /// with a genuine compare-and-swap) must provide a real atomic implementation — a persistent
    /// store typically does this with a conditional update (e.g. <c>UPDATE ... WHERE StateVersion =
    /// @expected</c>).
    /// </summary>
    bool TrySaveIfVersionMatches(ServiceRequest instance, int expectedStateVersion)
    {
        var exists = TryGet(instance.InstanceId, out var current);
        if (exists ? current.StateVersion != expectedStateVersion : expectedStateVersion != 0)
        {
            return false;
        }

        Save(instance);
        return true;
    }
}
