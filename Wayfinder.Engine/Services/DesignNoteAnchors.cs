using Wayfinder.Engine.Models;
using Wayfinder.Extensions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Where a note's anchor is in the current version of its blueprint: a readable label, and the same
/// path shape validation diagnostics use (<c>stages.check.routes[0]</c>). <see cref="Removed"/> is
/// true when the thing it was pinned to has gone, in which case the note stands at the nearest level
/// that still exists (the stage, for a removed field; otherwise the whole blueprint).
/// </summary>
public sealed record ResolvedDesignNoteAnchor(string Label, string Path, bool Removed);

internal static class DesignNoteAnchors
{
    private static readonly ResolvedDesignNoteAnchor WholeBlueprint = new("Whole blueprint", "", false);

    public static ResolvedDesignNoteAnchor Resolve(ServiceBlueprint blueprint, DesignNoteAnchor anchor) => anchor.Kind switch
    {
        DesignNoteAnchorKind.Stage => Stage(blueprint, anchor.Key),
        DesignNoteAnchorKind.Gateway => Gateway(blueprint, anchor.Key),
        DesignNoteAnchorKind.Route => Route(blueprint, anchor.Key),
        DesignNoteAnchorKind.Field => Field(blueprint, anchor.Key, anchor.FieldKey),
        _ => WholeBlueprint,
    };

    /// <summary>Why a new note can't be pinned here, or null if it can.</summary>
    public static string? Problem(ServiceBlueprint blueprint, DesignNoteAnchor anchor)
    {
        if (anchor.Kind == DesignNoteAnchorKind.Blueprint)
        {
            return anchor.Key is null && anchor.FieldKey is null ? null : "A blueprint-level note takes no key or fieldKey.";
        }
        if (anchor.Kind != DesignNoteAnchorKind.Field && anchor.FieldKey is not null)
        {
            return "Only a field anchor takes a fieldKey.";
        }
        return Resolve(blueprint, anchor).Removed
            ? $"There is no {anchor.Kind.ToString().ToLowerInvariant()} '{Describe(anchor)}' in blueprint '{blueprint.DefinitionKey}'."
            : null;
    }

    private static string Describe(DesignNoteAnchor anchor) =>
        anchor.FieldKey is null ? anchor.Key ?? "" : $"{anchor.Key}/{anchor.FieldKey}";

    private static ResolvedDesignNoteAnchor Removed(string what) => new($"Whole blueprint ({what} was removed)", "", true);

    private static ResolvedDesignNoteAnchor Stage(ServiceBlueprint blueprint, string? key) =>
        blueprint.Stages.FirstOrDefault(s => s.StageKey == key) is { } stage
            ? new($"Stage \"{stage.DisplayName}\"", $"stages.{stage.StageKey}", false)
            : Removed($"stage \"{key}\"");

    private static ResolvedDesignNoteAnchor Gateway(ServiceBlueprint blueprint, string? key) =>
        blueprint.Gateways?.FirstOrDefault(g => g.Key == key) is { } gateway
            ? new($"Gateway \"{gateway.DisplayName}\"", $"gateways.{gateway.Key}", false)
            : Removed($"gateway \"{key}\"");

    private static ResolvedDesignNoteAnchor Route(ServiceBlueprint blueprint, string? routeId)
    {
        var owners = blueprint.Stages.Select(s => (Path: $"stages.{s.StageKey}", Name: s.DisplayName, s.Routes))
            .Concat((blueprint.Gateways ?? []).Select(g => (Path: $"gateways.{g.Key}", Name: g.DisplayName, g.Routes)));
        foreach (var (path, name, routes) in owners)
        {
            var index = routes?.ToList().FindIndex(r => r.Id == routeId) ?? -1;
            if (index >= 0)
            {
                return new($"Route \"{routes![index].Trigger}\" from \"{name}\"", $"{path}.routes[{index}]", false);
            }
        }
        return Removed($"route \"{routeId}\"");
    }

    private static ResolvedDesignNoteAnchor Field(ServiceBlueprint blueprint, string? stageKey, string? fieldKey)
    {
        var stage = Stage(blueprint, stageKey);
        if (stage.Removed)
        {
            return stage;
        }
        var stageDefinition = blueprint.Stages.First(s => s.StageKey == stageKey);
        foreach (var (component, path) in stageDefinition.Components.FlattenWithPaths($"{stage.Path}.components"))
        {
            if (component is InputComponent input && input.FieldKey == fieldKey)
            {
                var name = string.IsNullOrWhiteSpace(input.Label) ? input.FieldKey : input.Label;
                return new($"Field \"{name}\" on \"{stageDefinition.DisplayName}\"", path, false);
            }
        }
        return new($"{stage.Label} (field \"{fieldKey}\" was removed)", stage.Path, true);
    }
}
