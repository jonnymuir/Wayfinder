namespace Wayfinder.Engine.Models;

// Request bodies for a host's design-note routes (Wayfinder.Engine.Api's, or a CMS's own), shared so
// the editor's HttpDesignNoteSource sends the same shape to either.

public sealed record AddDesignNoteRequest(DesignNoteAnchor Anchor, string Text);

public sealed record DesignNoteTextRequest(string Text);

public sealed record ResolveDesignNoteRequest(string Summary, int? Version = null);

public sealed record ReopenDesignNoteRequest(string? Reason = null);
