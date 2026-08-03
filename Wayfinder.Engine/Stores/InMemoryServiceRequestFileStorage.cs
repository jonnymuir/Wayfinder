using System.Collections.Concurrent;
using Wayfinder.Engine.Abstractions;

namespace Wayfinder.Engine.Stores;

/// <summary>
/// Default <see cref="IServiceRequestFileStorage"/> — process-lifetime only, matching this
/// toolkit's other in-memory defaults. A real host backs this with blob storage, a database, or
/// disk instead; nothing here survives a restart, which is the point for a transient/demo host.
/// </summary>
public sealed class InMemoryServiceRequestFileStorage : IServiceRequestFileStorage
{
    private readonly ConcurrentDictionary<string, (string FileName, byte[] Content)> _filesByReference = new();

    public Task<string> SaveAsync(string instanceId, string fieldKey, Stream content, string fileName, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var reference = $"memory://{instanceId}/{fieldKey}/{Guid.NewGuid():N}/{fileName}";
        _filesByReference[reference] = (fileName, buffer.ToArray());

        return Task.FromResult(reference);
    }

    public Task<Stream?> OpenReadAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(_filesByReference.TryGetValue(reference, out var file)
            ? (Stream)new MemoryStream(file.Content, writable: false)
            : null);
}
