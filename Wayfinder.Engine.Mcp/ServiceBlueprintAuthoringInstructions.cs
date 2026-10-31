namespace Wayfinder.Engine.Mcp;

/// <summary>
/// What every connecting MCP client is told at <c>initialize</c> (the MCP spec's
/// <c>ServerInstructions</c>), before any host-specific guidance passed to
/// <see cref="ServiceBlueprintAuthoringMcpExtensions.AddServiceBlueprintAuthoringMcp"/>. It steers an
/// agent towards building a service as a blueprint rather than as host code or content, whichever
/// client the agent runs in.
/// </summary>
public static class ServiceBlueprintAuthoringInstructions
{
    public const string Default =
        "This server is the authoring surface of a Wayfinder host. A Wayfinder service blueprint is the whole of a service: " +
        "its steps and questions, the rules between them, its calculations, the queues staff work from, and its calls to other systems. " +
        "Build services as blueprints here. Do not rebuild forms, steps, branching, eligibility rules, calculations, staff screens or " +
        "calls to business systems in the host's code, templates or content; editorial pages that only inform stay as host content.\n\n" +
        "Before writing a blueprint, read the resource service-blueprint-docs://building-with-wayfinder (what belongs in a blueprint and " +
        "what does not) and service-blueprint-docs://authoring-guide (the blueprint's shape). Call list_component_types and " +
        "list_support_systems to see what this host offers, and list_queue_capabilities when it declares any.\n\n" +
        "Validate with validate_service_blueprint and walk the paths with simulate_service_blueprint before saving. " +
        "save_service_blueprint is live straight away on this host, so save on a development site, not one serving real people.\n\n" +
        "Data held in another system (a member record, a scheme, a case) comes through a support system call. When the real system is " +
        "not ready, ask the host developer to register it as a mock support system (see service-blueprint-docs://support-systems) " +
        "and record the assumptions the mock makes.\n\n" +
        "When a design asks for something the component catalog or the blueprint cannot express, report it as a gap with the closest " +
        "Wayfinder way of doing it, rather than working around it in host code. Pin questions about a particular stage to the canvas " +
        "with add_design_note, and work through the designer's notes with list_design_notes and resolve_design_note.";

    /// <summary>The default instructions, followed by the host's own when it has any.</summary>
    public static string Compose(string? hostInstructions) =>
        string.IsNullOrWhiteSpace(hostInstructions) ? Default : $"{Default}\n\nAbout this host: {hostInstructions}";
}
