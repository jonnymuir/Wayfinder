namespace Wayfinder.Engine.Abstractions;

/// <summary>
/// The toolkit's extension point for <c>file-upload</c> field content. A host saves the
/// uploaded file itself and passes only the resulting reference string into <c>fieldValues</c>
/// for <see cref="IProcessManager.Advance"/> — the engine never sees raw bytes, the same way it
/// never sees anything else about how a field's value came to be. Size/type enforcement against
/// a field's declared <c>MaxSizeBytes</c>/<c>AcceptedFileTypes</c> is the host's job too, done
/// before calling <see cref="SaveAsync"/> — this interface has no opinion on either.
/// </summary>
public interface IServiceRequestFileStorage
{
    /// <summary>Saves an uploaded file for one field on one instance, returning the reference
    /// string to store as that field's value.</summary>
    Task<string> SaveAsync(string instanceId, string fieldKey, Stream content, string fileName, CancellationToken ct = default);

    /// <summary>Opens a previously saved file for reading by its reference string. Null if the
    /// reference doesn't resolve to anything stored.</summary>
    Task<Stream?> OpenReadAsync(string reference, CancellationToken ct = default);
}
