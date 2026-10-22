using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Stores;

/// <summary>Default <see cref="IDesignNoteStore"/>: process-lifetime only, like every other in-memory default here.</summary>
public sealed class InMemoryDesignNoteStore : IDesignNoteStore
{
    private readonly object _lock = new();
    private readonly List<DesignNote> _notes = [];

    public Task<IReadOnlyList<DesignNote>> ListAsync(string blueprintKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DesignNote>>(
                _notes.Where(n => n.BlueprintKey == blueprintKey).OrderBy(n => n.CreatedAt).ToArray());
        }
    }

    public Task<DesignNote?> GetAsync(string blueprintKey, string noteId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Find(blueprintKey, noteId));
        }
    }

    public Task AddAsync(DesignNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        lock (_lock)
        {
            if (Find(note.BlueprintKey, note.Id) is not null)
            {
                throw new InvalidOperationException($"Design note '{note.Id}' already exists.");
            }
            _notes.Add(note with { Revision = 1 });
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(DesignNote note, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        lock (_lock)
        {
            var index = _notes.FindIndex(n => n.BlueprintKey == note.BlueprintKey && n.Id == note.Id);
            if (index < 0 || _notes[index].Revision != expectedRevision)
            {
                return Task.FromResult(false);
            }
            _notes[index] = note with { Revision = expectedRevision + 1 };
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(string blueprintKey, string noteId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_notes.RemoveAll(n => n.BlueprintKey == blueprintKey && n.Id == noteId) > 0);
        }
    }

    public Task DeleteAllAsync(string blueprintKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _notes.RemoveAll(n => n.BlueprintKey == blueprintKey);
        }
        return Task.CompletedTask;
    }

    private DesignNote? Find(string blueprintKey, string noteId) =>
        _notes.FirstOrDefault(n => n.BlueprintKey == blueprintKey && n.Id == noteId);
}
