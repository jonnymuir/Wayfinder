using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Extensions;

/// <summary>
/// Tree-walking helpers for the v2.0 polymorphic <see cref="Component"/> hierarchy. Driven
/// generically by each component's own <see cref="ComponentDescriptor.Containment"/> (via
/// <see cref="ComponentTypeRegistry"/>) instead of a hand-maintained switch — the previous
/// version's switch never descended into <c>SummaryListComponent.Children</c>, structurally
/// identical to <c>FieldsetComponent.Children</c>, simply because nobody had added a case for
/// it. A registered custom container type works here automatically, with no change to this file.
/// </summary>
public static class ComponentExtensions
{
    // PropertyInfo lookups are cached per (Type, propertyName) — this runs on every tree walk,
    // so avoiding a fresh reflection lookup per call matters once a blueprint has any real depth.
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> PropertyCache = new();

    private static PropertyInfo GetCachedProperty(Type type, string propertyName) =>
        PropertyCache.GetOrAdd((type, propertyName), key =>
            key.Item1.GetProperty(key.Item2)
            ?? throw new InvalidOperationException(
                $"Containment declares property '{key.Item2}' on {key.Item1.Name}, but no such property exists."));

    // Path segments should address the JSON a diagnostic's reader is actually looking at
    // (ServiceBlueprintJson.WriteOptions uses JsonNamingPolicy.CamelCase), not the raw C#
    // PropertyInfo name a Containment descriptor declares.
    private static string CamelCase(string propertyName) =>
        propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    /// <summary>
    /// Yields <paramref name="component"/>'s direct children (only — not further descendants),
    /// each with a path segment appended to <paramref name="basePath"/> addressing it, per its
    /// registered <see cref="ComponentDescriptor.Containment"/>. The one place every containment
    /// shape (<see cref="ContainmentKind.ChildList"/>/<see cref="ContainmentKind.NamedSections"/>/
    /// <see cref="ContainmentKind.KeyedChildren"/>) is actually read via reflection — every other
    /// tree-walker in this file is built on top of this.
    /// </summary>
    private static IEnumerable<(Component Child, string Path)> DirectChildren(Component component, string basePath)
    {
        var containment = ComponentTypeRegistry.DescriptorFor(component).Containment;
        var type = component.GetType();

        switch (containment.Kind)
        {
            case ContainmentKind.None:
                yield break;

            case ContainmentKind.ChildList:
            {
                var children = (IReadOnlyList<Component>?)GetCachedProperty(type, containment.PropertyName!).GetValue(component);
                if (children is null)
                {
                    yield break;
                }

                var propertySegment = CamelCase(containment.PropertyName!);
                for (var i = 0; i < children.Count; i++)
                {
                    yield return (children[i], $"{basePath}.{propertySegment}[{i}]");
                }

                break;
            }

            case ContainmentKind.NamedSections:
            {
                var sections = (IEnumerable?)GetCachedProperty(type, containment.PropertyName!).GetValue(component);
                if (sections is null)
                {
                    yield break;
                }

                var sectionsSegment = CamelCase(containment.PropertyName!);
                var childrenSegment = CamelCase(containment.SectionChildrenPropertyName!);
                var sectionIndex = 0;
                foreach (var section in sections)
                {
                    var children = (IReadOnlyList<Component>?)GetCachedProperty(
                        section!.GetType(), containment.SectionChildrenPropertyName!).GetValue(section);
                    if (children is not null)
                    {
                        for (var i = 0; i < children.Count; i++)
                        {
                            yield return (children[i], $"{basePath}.{sectionsSegment}[{sectionIndex}].{childrenSegment}[{i}]");
                        }
                    }

                    sectionIndex++;
                }

                break;
            }

            case ContainmentKind.KeyedChildren:
            {
                var byKey = (IReadOnlyDictionary<string, IReadOnlyList<Component>>?)
                    GetCachedProperty(type, containment.PropertyName!).GetValue(component);
                if (byKey is null)
                {
                    yield break;
                }

                var propertySegment = CamelCase(containment.PropertyName!);
                foreach (var (key, children) in byKey)
                {
                    for (var i = 0; i < children.Count; i++)
                    {
                        yield return (children[i], $"{basePath}.{propertySegment}.{key}[{i}]");
                    }
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled {nameof(ContainmentKind)} '{containment.Kind}'.");
        }
    }

    /// <summary>
    /// Recursively walks the component tree and yields every component, including
    /// descendants nested inside any registered container type at any depth.
    /// </summary>
    public static IEnumerable<Component> Flatten(this IEnumerable<Component> components) =>
        components.FlattenWithPaths("$").Select(entry => entry.Component);

    /// <summary>
    /// Returns every <see cref="InputComponent"/> in the tree, regardless of nesting depth.
    /// Includes components nested inside a <see cref="ComponentCategory.DataDisplay"/> container
    /// (e.g. <c>SummaryListComponent.Children</c>) — for most callers (rendering a radio's
    /// conditional children, generic tree inspection) that's correct. For anything treating the
    /// result as "the set of fieldKeys that can genuinely receive a submitted value" — a
    /// calculation scope, or a dangling-reference check — use <see cref="GetSubmittableInputs"/>
    /// instead; see its own remarks for why the distinction matters.
    /// </summary>
    public static IEnumerable<InputComponent> GetAllInputs(this IEnumerable<Component> components)
        => components.Flatten().OfType<InputComponent>();

    /// <summary>
    /// Returns every <see cref="InputComponent"/> in the tree that represents a genuinely
    /// submittable value — excluding any nested inside a <see cref="ComponentCategory.DataDisplay"/>
    /// container (e.g. <c>SummaryListComponent.Children</c>, GOV.UK's check-your-answers pattern).
    /// A summary-list child reuses an <see cref="InputComponent"/>-derived type purely for
    /// rendering convenience (the same label/value row shape a real form field uses) — it never
    /// receives a submission of its own; it only ever projects a value that already exists
    /// elsewhere (an input captured on an earlier stage, or a calculated field), under the same
    /// fieldKey. Treating it as a second, genuine input double-counts one logical value, and —
    /// when that fieldKey also happens to be a <c>calculations.fields</c> entry, the standard way
    /// to echo a calculated result — caused two real bugs before this method existed:
    /// <see cref="Wayfinder.Services.Calculations.CalculationScopeBuilder.DescribeInputs"/> would
    /// add the echoed fieldKey to the calc scope from a resubmitted display value, so evaluating
    /// the same-named calculated field then threw "Field 'x' collides with an input or earlier
    /// field"; and <see cref="Wayfinder.Models.ServiceDesign.ServiceBlueprint.ValidateDataDisplayBindings"/>'s
    /// "is this a known field" check considered a summary-list child's own fieldKey self-evidently valid (it's
    /// right there in the "known inputs" set, because it put itself there), so a genuinely
    /// dangling binding — one that doesn't resolve to any real input or calculated field — was
    /// silently never flagged. Every caller that needs "can this fieldKey actually hold a
    /// submitted value" — not just "does an <see cref="InputComponent"/> exist with this name
    /// somewhere in the tree" — should use this instead of <see cref="GetAllInputs"/>.
    /// </summary>
    public static IEnumerable<InputComponent> GetSubmittableInputs(this IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            foreach (var found in WalkSubmittableInputs(component))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<InputComponent> WalkSubmittableInputs(Component component)
    {
        if (component is InputComponent input)
        {
            yield return input;
        }

        // A DataDisplay container's children exist purely to project an already-known value —
        // never to receive one of their own — so don't descend into them here, regardless of
        // what CLR type they happen to reuse for rendering.
        if (ComponentTypeRegistry.DescriptorFor(component).Category == ComponentCategory.DataDisplay)
        {
            yield break;
        }

        foreach (var (child, _) in DirectChildren(component, ""))
        {
            foreach (var found in WalkSubmittableInputs(child))
            {
                yield return found;
            }
        }
    }

    /// <summary>
    /// Recursively walks the component tree like <see cref="Flatten"/>, but also yields each
    /// component's document path (e.g. <c>stages.review.components[2].children[0]</c>) rooted
    /// at <paramref name="basePath"/>, for callers that need to address a specific component in
    /// diagnostics.
    /// </summary>
    public static IEnumerable<(Component Component, string Path)> FlattenWithPaths(
        this IEnumerable<Component> components, string basePath)
    {
        var index = 0;
        foreach (var component in components)
        {
            foreach (var entry in WalkWithPath(component, $"{basePath}[{index}]"))
            {
                yield return entry;
            }

            index++;
        }
    }

    private static IEnumerable<(Component Component, string Path)> WalkWithPath(Component component, string path)
    {
        yield return (component, path);

        foreach (var (child, childPath) in DirectChildren(component, path))
        {
            foreach (var entry in WalkWithPath(child, childPath))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Returns the first descendant of type <typeparamref name="T"/> in the tree, or null if none exists.
    /// </summary>
    public static T? FindFirst<T>(this IEnumerable<Component> components) where T : Component
        => components.Flatten().OfType<T>().FirstOrDefault();

    /// <summary>
    /// Infers the GDS step type for a stage based on the components it contains.
    /// Replaces the V1 <c>EffectiveStepType</c> property which lived on the stage record.
    /// </summary>
    public static string InferStepType(this IEnumerable<Component> components)
    {
        var list = components as IReadOnlyCollection<Component> ?? components.ToArray();

        if (list.OfType<WaitingComponent>().Any())
            return "status-timeline";
        if (list.OfType<PanelComponent>().Any())
            return "confirmation";
        if (list.OfType<SummaryListComponent>().Any())
            return "check-answers";
        if (list.OfType<TaskListComponent>().Any())
            return "task-list";
        return "question";
    }
}
