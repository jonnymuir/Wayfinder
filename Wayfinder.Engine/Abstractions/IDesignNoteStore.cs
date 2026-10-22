using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// Persists design notes, beside the blueprints they are about. The engine ships
/// <see cref="Stores.InMemoryDesignNoteStore"/>; a host that wants notes to survive a restart
/// implements this against its own persistence, the same way it implements
/// <see cref="IServiceBlueprintSourceStore"/>.
/// </summary>
public interface IDesignNoteStore
{
    /// <summary>Every note on one blueprint, oldest first.</summary>
    Task<IReadOnlyList<DesignNote>> ListAsync(string blueprintKey, CancellationToken ct = default);

    Task<DesignNote?> GetAsync(string blueprintKey, string noteId, CancellationToken ct = default);

    Task AddAsync(DesignNote note, CancellationToken ct = default);

    /// <summary>
    /// Replaces a note only if its stored <see cref="DesignNote.Revision"/> is still
    /// <paramref name="expectedRevision"/>, storing it at <paramref name="expectedRevision"/> + 1.
    /// Returns false on a mismatch or when the note no longer exists. Implementations MUST perform the
    /// compare and the write atomically (<c>UPDATE ... WHERE Revision = @expectedRevision</c>).
    /// </summary>
    Task<bool> UpdateAsync(DesignNote note, int expectedRevision, CancellationToken ct = default);

    /// <summary>Returns false if the note didn't exist.</summary>
    Task<bool> DeleteAsync(string blueprintKey, string noteId, CancellationToken ct = default);

    /// <summary>Removes every note on a blueprint, when the blueprint itself is deleted.</summary>
    Task DeleteAllAsync(string blueprintKey, CancellationToken ct = default);
}
