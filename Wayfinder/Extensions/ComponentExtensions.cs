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
    /// </summary>
    public static IEnumerable<InputComponent> GetAllInputs(this IEnumerable<Component> components)
        => components.Flatten().OfType<InputComponent>();

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
