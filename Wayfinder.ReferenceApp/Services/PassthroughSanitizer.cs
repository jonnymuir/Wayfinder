using Wayfinder.Services.Sanitization;

namespace Wayfinder.ReferenceApp.Services;

/// <summary>Seed content is developer-authored, not user-supplied — no XSS risk, so passthrough is fine here.</summary>
public sealed class PassthroughSanitizer : IServiceContentSanitizer
{
    public string Sanitize(string? html) => html ?? string.Empty;
}
