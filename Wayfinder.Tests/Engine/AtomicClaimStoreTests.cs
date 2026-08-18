using FluentAssertions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Stores;

namespace Wayfinder.Tests.Engine;

/// <summary>
/// <see cref="InMemoryServiceRequestStore.TrySaveIfVersionMatches"/> — the toolkit's sole
/// compare-and-swap primitive, the foundation the claim/ownership work-allocation feature (see
/// docs/guides/work-allocation.md) builds atomic claiming on. Must be genuinely safe against real
/// concurrent callers, not merely "works because tests are single-threaded."
/// </summary>
public class AtomicClaimStoreTests
{
    private static ServiceRequest NewInstance(string instanceId, int stateVersion) => new()
    {
        InstanceId = instanceId,
        BlueprintKey = "test",
        TenantId = "tenant",
        UserId = "user",
        CurrentStage = "start",
        StateVersion = stateVersion,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void FirstSave_SucceedsOnlyWhenExpectedVersionIsZero()
    {
        var store = new InMemoryServiceRequestStore();
        var instance = NewInstance("i1", 0);

        store.TrySaveIfVersionMatches(instance, expectedStateVersion: 1).Should().BeFalse(
            "no instance exists yet, so only expectedStateVersion 0 (a genuine first save) may succeed");
        store.TryGet("i1", out _).Should().BeFalse();

        store.TrySaveIfVersionMatches(instance, expectedStateVersion: 0).Should().BeTrue();
        store.TryGet("i1", out var saved).Should().BeTrue();
        saved.StateVersion.Should().Be(0);
    }

    [Fact]
    public void SubsequentSave_SucceedsOnlyWhenExpectedVersionMatchesStored()
    {
        var store = new InMemoryServiceRequestStore();
        store.TrySaveIfVersionMatches(NewInstance("i1", 0), expectedStateVersion: 0).Should().BeTrue();

        store.TrySaveIfVersionMatches(NewInstance("i1", 5), expectedStateVersion: 5).Should().BeFalse(
            "the stored version is still 0, not 5 — a caller working from stale data must lose");

        store.TrySaveIfVersionMatches(NewInstance("i1", 1), expectedStateVersion: 0).Should().BeTrue();
        store.TryGet("i1", out var saved).Should().BeTrue();
        saved.StateVersion.Should().Be(1);
    }

    [Fact]
    public void ConcurrentRacers_ExactlyOneWinsEachRound()
    {
        var store = new InMemoryServiceRequestStore();
        store.TrySaveIfVersionMatches(NewInstance("i1", 0), expectedStateVersion: 0).Should().BeTrue();

        const int racerCount = 10;
        var barrier = new Barrier(racerCount);
        var results = new bool[racerCount];

        Parallel.For(0, racerCount, i =>
        {
            barrier.SignalAndWait(); // maximize genuine overlap, not a sequential loop
            results[i] = store.TrySaveIfVersionMatches(NewInstance("i1", 1), expectedStateVersion: 0);
        });

        results.Count(r => r).Should().Be(1, "every racer read the same stored version 0 — exactly one may win the swap to 1");
        store.TryGet("i1", out var final).Should().BeTrue();
        final.StateVersion.Should().Be(1, "the winner's write must be the one that actually landed");
    }

    [Fact]
    public void ConcurrentRacers_AcrossManyRoundsNeverDoubleAdvance()
    {
        // A tighter, repeated version of the single-round test above — proves the primitive holds
        // up over many rounds of real contention, not just once.
        var store = new InMemoryServiceRequestStore();
        store.TrySaveIfVersionMatches(NewInstance("i1", 0), expectedStateVersion: 0).Should().BeTrue();

        for (var round = 0; round < 20; round++)
        {
            store.TryGet("i1", out var before).Should().BeTrue();
            var expected = before.StateVersion;
            var barrier = new Barrier(10);
            var wins = 0;

            Parallel.For(0, 10, _ =>
            {
                barrier.SignalAndWait();
                if (store.TrySaveIfVersionMatches(NewInstance("i1", expected + 1), expectedStateVersion: expected))
                {
                    Interlocked.Increment(ref wins);
                }
            });

            wins.Should().Be(1, $"round {round}: exactly one of 10 concurrent racers may advance the version");
            store.TryGet("i1", out var after).Should().BeTrue();
            after.StateVersion.Should().Be(expected + 1);
        }
    }
}
