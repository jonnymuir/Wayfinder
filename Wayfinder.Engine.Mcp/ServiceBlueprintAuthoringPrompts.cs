using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Wayfinder.Engine.Mcp;

/// <summary>
/// Ready-made MCP prompts for the three ways a design partner usually starts: a new service, an
/// existing design or site to rebuild the Wayfinder way, and a round of the designer's notes. A
/// client shows them as starters (Claude Code lists them as slash commands), so the working
/// method travels with the server rather than with any one client's configuration.
/// </summary>
[McpServerPromptType]
public static class ServiceBlueprintAuthoringPrompts
{
    [McpServerPrompt(Name = "design-a-service")]
    [Description("Design a new service as a Wayfinder service blueprint, from a plain-language brief.")]
    public static string DesignAService(
        [Description("The service in plain language: who it is for, what they need, and the rules that apply.")] string brief) =>
        $"""
        Design this service as a Wayfinder service blueprint:

        {brief}

        Work like this:
        1. Read service-blueprint-docs://building-with-wayfinder, service-blueprint-docs://authoring-guide and
           service-blueprint-docs://service-design-principles.
        2. Call list_component_types and list_support_systems to see what this host offers.
        3. Before writing anything, tell me the queues (who does the work), the stages each person goes through,
           the decisions between them and any data that comes from another system. Ask me about anything the
           brief leaves open.
        4. Write the blueprint, then validate_service_blueprint and simulate_service_blueprint every route,
           including the unhappy ones. Fix what they find.
        5. Save it, and tell me where to try it. Pin anything still open to the stage it concerns with
           add_design_note.
        """;

    [McpServerPrompt(Name = "convert-an-existing-design")]
    [Description("Rebuild an existing design, prototype or site the Wayfinder way: blueprints for every journey, host content only for editorial pages, and a list of gaps.")]
    public static string ConvertAnExistingDesign(
        [Description("Where the design is, or a description of it: its pages, journeys, staff screens and the data it shows.")] string design) =>
        $"""
        Rebuild this design the Wayfinder way, with as little code as possible and as much of it as possible in
        service blueprints:

        {design}

        Work like this, and stop after step 5 for my decisions:
        1. Read service-blueprint-docs://building-with-wayfinder and service-blueprint-docs://authoring-guide.
        2. Inventory the design: every page and interaction, each given a kind: editorial content, a stage in a
           journey, a staff task, a data lookup, a notification, or styling.
        3. Group the stages into services, one blueprint per service, and name each blueprint's queues.
        4. List every value shown that the person did not type, and where it comes from: the signed-in person
           (a service input) or another system (a support system, mocked until the real one exists). Write down
           what each mock assumes about the real system.
        5. Report the gaps: anything the blueprint, the component catalog (list_component_types) or the host's
           styling cannot express, each with the closest Wayfinder way of doing it.
        6. Once I have decided, build blueprint first: write, validate, simulate and save each blueprint. Host
           content and styling come last.
        """;

    [McpServerPrompt(Name = "work-through-design-notes")]
    [Description("Work through the open design notes on a blueprint: change it, save it, and resolve each note with what was done.")]
    public static string WorkThroughDesignNotes(
        [Description("The blueprint's definitionKey.")] string definitionKey) =>
        $"""
        Work through the open design notes on the blueprint "{definitionKey}".

        1. list_design_notes for it, and read_service_blueprint.
        2. For each note, change the blueprint to address it. When a note needs a decision you cannot make,
           reply_to_design_note with the question instead of guessing.
        3. validate_service_blueprint, simulate_service_blueprint the routes you changed, then save_service_blueprint.
        4. resolve_design_note for each note you addressed, with a sentence on what you did and the saved version.
        5. Tell me what changed and which notes are still open.
        """;
}
