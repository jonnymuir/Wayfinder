using System.Collections.Concurrent;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Stores;

/// <summary>
/// Default <see cref="IServiceRequestStore"/> — process-lifetime only, exactly matching
/// <see cref="Services.ProcessManagerEngine"/>'s original hardcoded behaviour before the
/// store became a pluggable seam. Used whenever a host doesn't supply its own.
/// </summary>
public sealed class InMemoryServiceRequestStore : IServiceRequestStore
{
    private readonly ConcurrentDictionary<string, ServiceRequest> _instancesById = new();

    public bool TryGet(string instanceId, out ServiceRequest instance) =>
        _instancesById.TryGetValue(instanceId, out instance!);

    public void Save(ServiceRequest instance) =>
        _instancesById[instance.InstanceId] = instance;

    public bool Remove(string instanceId) =>
        _instancesById.TryRemove(instanceId, out _);

    public void Clear() => _instancesById.Clear();

    public IEnumerable<ServiceRequest> GetAll() => _instancesById.Values;

    /// <summary>
    /// A genuine compare-and-swap, unlike <see cref="IServiceRequestStore"/>'s own default
    /// implementation — <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> only swaps if the
    /// currently-stored reference still equals <c>current</c>, and <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
    /// only inserts if no entry exists yet — both hardware-CAS-backed, safe against real concurrent
    /// callers, not merely "works because nothing else happens to run between the check and the
    /// write" the way a plain indexer read-then-write would be.
    /// </summary>
    public bool TrySaveIfVersionMatches(ServiceRequest instance, int expectedStateVersion)
    {
        if (!_instancesById.TryGetValue(instance.InstanceId, out var current))
        {
            return expectedStateVersion == 0 && _instancesById.TryAdd(instance.InstanceId, instance);
        }

        return current.StateVersion == expectedStateVersion
            && _instancesById.TryUpdate(instance.InstanceId, instance, current);
    }
}
