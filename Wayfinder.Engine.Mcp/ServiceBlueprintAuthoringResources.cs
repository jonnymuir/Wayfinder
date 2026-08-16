using System.Reflection;
using ModelContextProtocol.Server;

namespace Wayfinder.Engine.Mcp;

/// <summary>
/// MCP resources exposing the service blueprint authoring reference docs — the calculation
/// expression grammar, the full ServiceBlueprint contract, the general
/// service design principles a blueprint should be judged against, and how to extend the
/// component catalog or register a support system — so an agent
/// connected only over MCP (no repo checkout) can fetch them directly. Content is
/// embedded from the canonical, tool-agnostic markdown in docs/guides/ at build
/// time; there is no separate copy to keep in sync.
/// </summary>
[McpServerResourceType]
public static class ServiceBlueprintAuthoringResources
{
    [McpServerResource(
        Name = "calculation-language",
        UriTemplate = "service-blueprint-docs://calculation-language",
        Title = "Wayfinder Calculation Expression Language",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "Grammar, function reference (if/min/max/clamp/abs/floor/round/pow/lookup), tables/series " +
        "semantics, decimal numeric semantics, showWhen visibility expressions, and a worked walkthrough " +
        "for the declarative calculation expression language used in a blueprint's `calculations` block " +
        "and any component's `showWhen`.")]
    public static string CalculationLanguage() => ReadEmbeddedDoc("calculation-language.md");

    [McpServerResource(
        Name = "authoring-guide",
        UriTemplate = "service-blueprint-docs://authoring-guide",
        Title = "Wayfinder Blueprint Authoring Contract",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "The full ServiceBlueprint JSON contract: stages, routes, gateways and the gateway " +
        "routing rule, queues, the component catalog, response stages, and the save/conflict protocol.")]
    public static string AuthoringGuide() => ReadEmbeddedDoc("reference-service-blueprint-contract.md");

    [McpServerResource(
        Name = "service-design-principles",
        UriTemplate = "service-blueprint-docs://service-design-principles",
        Title = "Service Design Principles",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "Industry-agnostic service design grounding for whoever is authoring a blueprint: the Design " +
        "Council Double Diamond process, the GOV.UK Service Standard, and Lou Downe's 15 principles of " +
        "good services, each mapped to concrete service-blueprint-authoring decisions. Does not cover sector-specific " +
        "regulation or domain best practice — bring that yourself alongside this resource.")]
    public static string ServiceDesignPrinciples() => ReadEmbeddedDoc("service-design-principles.md");

    [McpServerResource(
        Name = "ai-service-blueprint-authoring",
        UriTemplate = "service-blueprint-docs://ai-service-blueprint-authoring",
        Title = "AI-Ready Blueprint Authoring — Integrator Guide",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "How a host app wires this MCP surface into its own pipeline (MapServiceBlueprintAuthoringMcp, " +
        "IServiceBlueprintSourceStore, auth). For an integrator setting this up, not for an agent authoring a " +
        "blueprint — that agent wants authoring-guide, calculation-language, or service-design-principles " +
        "instead.")]
    public static string AiServiceBlueprintAuthoring() => ReadEmbeddedDoc("ai-service-blueprint-authoring.md");

    [McpServerResource(
        Name = "extending-the-component-catalog",
        UriTemplate = "service-blueprint-docs://extending-the-component-catalog",
        Title = "Extending the Component Catalog",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "How a toolkit integrator registers a genuinely new Component type — ComponentDescriptor/" +
        "ComponentPropertyDescriptor shape, containment shapes, ComponentTypeRegistry.Register timing, " +
        "the GovUkComponentRenderer pairing, and a full worked example. For whoever is building a " +
        "Wayfinder host, not whoever is authoring a blueprint against one — that agent wants " +
        "authoring-guide instead.")]
    public static string ExtendingTheComponentCatalog() => ReadEmbeddedDoc("extending-the-component-catalog.md");

    [McpServerResource(
        Name = "support-systems",
        UriTemplate = "service-blueprint-docs://support-systems",
        Title = "Support Systems",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "How Wayfinder models the third NN/g service-blueprint lane — external/downstream systems a " +
        "backstage actor calls out to. SupportSystemDescriptor/SupportSystemCapabilityDescriptor shape, " +
        "the capability-declared poll/webhook completion-mode abstraction, SupportSystemRegistry.Register " +
        "timing, and the support-system-call action convention. For whoever is building a Wayfinder host, " +
        "not whoever is authoring a blueprint against one — that agent wants authoring-guide instead.")]
    public static string SupportSystems() => ReadEmbeddedDoc("support-systems.md");

    [McpServerResource(
        Name = "bulk-data-review",
        UriTemplate = "service-blueprint-docs://bulk-data-review",
        Title = "Bulk Data Review",
        MimeType = "text/markdown")]
    [System.ComponentModel.Description(
        "How Wayfinder handles bulk, row-level data — a paginated 'only show me what needs attention' " +
        "review experience layered on top of Support Systems, for an external system that only ever " +
        "speaks whole-file-in/whole-file-out. The bulk-dataset-ingest/bulk-dataset-materialize action " +
        "convention, column role vocabulary (RowKey/Data/ResponseMatchedId/ResponseError/ResponseWarning), " +
        "and how a BulkDataReviewComponent binds to the resulting dataset. For whoever is authoring a " +
        "blueprint against a host with IBulkDatasetStore registered.")]
    public static string BulkDataReview() => ReadEmbeddedDoc("bulk-data-review.md");

    private static string ReadEmbeddedDoc(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{typeof(ServiceBlueprintAuthoringResources).Namespace}.docs.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded doc '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
