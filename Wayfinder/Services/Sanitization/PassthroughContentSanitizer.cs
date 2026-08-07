namespace Wayfinder.Services.Sanitization;

/// <summary>
/// Identity sanitizer — returns its input unchanged (empty string for null). Only ever safe for
/// content that never came from a real, untrusted author: developer-authored seed data, a
/// dry-run/simulation engine with no real request behind it, or a test fixture. A host rendering
/// real backoffice- or user-authored content must supply its own <see cref="IServiceContentSanitizer"/>
/// (e.g. an HTML allowlist) instead — never register this one for production rendering.
/// </summary>
public sealed class PassthroughContentSanitizer : IServiceContentSanitizer
{
    public string Sanitize(string? html) => html ?? string.Empty;
}
