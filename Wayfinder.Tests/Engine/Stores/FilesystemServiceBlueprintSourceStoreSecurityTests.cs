using FluentAssertions;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Stores;

/// <summary>
/// Security regression cover for <see cref="FilesystemServiceBlueprintSourceStore"/>: a
/// definition key is turned straight into a filename, so an externally-supplied key that
/// isn't a plain slug (path separators, "..", an absolute path, a leading dot) must be
/// rejected before it ever reaches the filesystem — on every method that takes a key.
/// </summary>
public class FilesystemServiceBlueprintSourceStoreSecurityTests : IDisposable
{
    private readonly string _baseDir =
        Path.Combine(Path.GetTempPath(), "wf-source-store-sec-" + Guid.NewGuid().ToString("N"));

    public FilesystemServiceBlueprintSourceStoreSecurityTests() => Directory.CreateDirectory(_baseDir);

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    public static TheoryData<string> MaliciousKeys() =>
    [
        "../secret",
        "../../etc/passwd",
        "..\\..\\windows\\system32\\config",
        "sub/dir/key",
        "sub\\dir\\key",
        "/etc/passwd",
        ".ssh",
        "key with space",
        "key.with.dots",
        "",
    ];

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task LoadAsync_RejectsANonSlugDefinitionKey_WithoutTouchingTheFilesystem(string key)
    {
        var store = new FilesystemServiceBlueprintSourceStore(_baseDir);

        var act = () => store.LoadAsync(key);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task DeleteAsync_RejectsANonSlugDefinitionKey(string key)
    {
        var store = new FilesystemServiceBlueprintSourceStore(_baseDir);

        var act = () => store.DeleteAsync(key);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task SaveAsync_RejectsABlueprintWhoseDefinitionKeyIsNotASlug(string key)
    {
        var store = new FilesystemServiceBlueprintSourceStore(_baseDir);
        var blueprint = new ServiceBlueprint { DefinitionKey = key, DisplayName = "x", InitialStage = "only" };

        var act = () => store.SaveAsync(blueprint, expectedVersion: 0);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("juggling-licence")]
    [InlineData("njf_contributions")]
    [InlineData("route-show-when-gateway-diagnostics-test")]
    [InlineData("a")]
    [InlineData("A1")]
    public async Task LoadAsync_AcceptsAPlainSlugKey_AndReturnsNullWhenAbsent(string key)
    {
        var store = new FilesystemServiceBlueprintSourceStore(_baseDir);

        var result = await store.LoadAsync(key);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsForASlugKey()
    {
        var store = new FilesystemServiceBlueprintSourceStore(_baseDir);
        var blueprint = new ServiceBlueprint { DefinitionKey = "round-trip-key", DisplayName = "Round trip", InitialStage = "only" };

        var save = await store.SaveAsync(blueprint, expectedVersion: 0);
        save.Saved.Should().BeTrue();

        var loaded = await store.LoadAsync("round-trip-key");
        loaded.Should().NotBeNull();
        loaded!.DefinitionKey.Should().Be("round-trip-key");

        Directory.GetFiles(_baseDir).Should().ContainSingle()
            .Which.Should().EndWith("round-trip-key.json");
    }
}
