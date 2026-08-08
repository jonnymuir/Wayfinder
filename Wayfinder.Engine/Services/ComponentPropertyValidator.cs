using System.Collections;
using System.Text.RegularExpressions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Validates a live <see cref="Component"/> instance against its own registered
/// <see cref="ComponentDescriptor"/> — the generic replacement for hand-written per-type checks
/// that would otherwise need updating every time a property is added anywhere in the catalog
/// (built-in or third-party). Reflects <see cref="ComponentPropertyDescriptor.Key"/> straight
/// against the component's CLR property of the same name (the same convention
/// <see cref="BuiltInComponentDescriptors"/> itself relies on via <see langword="nameof"/>), so a
/// descriptor and its component can never silently disagree about which property it means.
/// </summary>
public static class ComponentPropertyValidator
{
    /// <summary>
    /// Validates every declared property on <paramref name="component"/> against
    /// <paramref name="descriptor"/>, plus (for a <see cref="ContainmentKind.KeyedChildren"/>
    /// container) that every conditional-child key is actually one of the values its sibling
    /// key-source property declares — e.g. a <c>RadiosComponent.ConditionalChildren</c> key that
    /// doesn't match any of its own <c>Options</c>, which today can be authored with nothing
    /// noticing the branch can never be reached.
    /// </summary>
    public static IEnumerable<ServiceBlueprintDiagnostic> Validate(
        Component component, ComponentDescriptor descriptor, string path)
    {
        foreach (var diagnostic in ValidateProperties(component, descriptor.Properties, path))
        {
            yield return diagnostic;
        }

        foreach (var diagnostic in ValidateConditionalChildKeys(component, descriptor.Containment, path))
        {
            yield return diagnostic;
        }
    }

    private static IEnumerable<ServiceBlueprintDiagnostic> ValidateConditionalChildKeys(
        Component component, ComponentContainment containment, string path)
    {
        if (containment is not { Kind: ContainmentKind.KeyedChildren, PropertyName: { } childrenProperty, KeySourceProperty: { } keySourceProperty })
        {
            yield break;
        }

        var type = component.GetType();
        var byKey = type.GetProperty(childrenProperty)?.GetValue(component)
            as IReadOnlyDictionary<string, IReadOnlyList<Component>>;
        if (byKey is null || byKey.Count == 0)
        {
            yield break;
        }

        var validKeys = type.GetProperty(keySourceProperty)?.GetValue(component) as IReadOnlyList<string> ?? [];
        var childrenSegment = CamelCase(childrenProperty);

        foreach (var key in byKey.Keys)
        {
            if (!validKeys.Contains(key, StringComparer.Ordinal))
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH",
                    $"{path}.{childrenSegment}.{key}",
                    $"'{key}' is a key in '{childrenProperty}' but not one of the values declared in " +
                    $"'{keySourceProperty}' — this branch can never be shown, since nothing can select it. " +
                    $"Declared values: {(validKeys.Count == 0 ? "(none)" : string.Join(", ", validKeys))}.");
            }
        }
    }

    private static IEnumerable<ServiceBlueprintDiagnostic> ValidateProperties(
        object instance, IReadOnlyList<ComponentPropertyDescriptor> properties, string path)
    {
        var type = instance.GetType();

        foreach (var property in properties)
        {
            var clrProperty = type.GetProperty(property.Key);
            if (clrProperty is null)
            {
                // A descriptor referencing a property that doesn't exist on its own CLR type is
                // an authoring bug in the descriptor itself, not something a blueprint author can
                // fix — surfacing it as a blueprint diagnostic would be the wrong audience.
                continue;
            }

            var value = clrProperty.GetValue(instance);
            var propertyPath = $"{path}.{CamelCase(property.Key)}";

            if (IsMissing(value))
            {
                if (property.Required)
                {
                    yield return new ServiceBlueprintDiagnostic(
                        "COMPONENT_PROPERTY_REQUIRED",
                        propertyPath,
                        $"'{property.Title}' is required on this component but is missing or empty.");
                }

                continue;
            }

            foreach (var diagnostic in ValidateConstraints(property, value, propertyPath))
            {
                yield return diagnostic;
            }

            if (property is { ValueKind: ComponentPropertyValueKind.Array, Items.Properties: { } itemProperties }
                && value is IEnumerable items and not string)
            {
                var index = 0;
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        foreach (var diagnostic in ValidateProperties(item, itemProperties, $"{propertyPath}[{index}]"))
                        {
                            yield return diagnostic;
                        }
                    }

                    index++;
                }
            }
            else if (property is { ValueKind: ComponentPropertyValueKind.Object, Properties: { } nestedProperties })
            {
                // Reached only past the IsMissing(value) check above, which treats null as
                // missing — value is guaranteed non-null here.
                foreach (var diagnostic in ValidateProperties(value!, nestedProperties, propertyPath))
                {
                    yield return diagnostic;
                }
            }
        }
    }

    private static IEnumerable<ServiceBlueprintDiagnostic> ValidateConstraints(
        ComponentPropertyDescriptor property, object? value, string propertyPath)
    {
        if (value is string stringValue)
        {
            if (property.AllowedValues is { Count: > 0 } allowedValues
                && !allowedValues.Contains(stringValue, StringComparer.Ordinal))
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_INVALID_VALUE",
                    propertyPath,
                    $"'{property.Title}' is '{stringValue}', which isn't one of the allowed values: " +
                    $"{string.Join(", ", allowedValues)}.");
            }

            if (property.Pattern is { Length: > 0 } pattern && !Regex.IsMatch(stringValue, pattern))
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_PATTERN_MISMATCH",
                    propertyPath,
                    $"'{property.Title}' value '{stringValue}' does not match the required pattern '{pattern}'.");
            }

            if (property.MinLength is { } minLength && stringValue.Length < minLength)
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_TOO_SHORT",
                    propertyPath,
                    $"'{property.Title}' must be at least {minLength} character(s) long, but is {stringValue.Length}.");
            }

            if (property.MaxLength is { } maxLength && stringValue.Length > maxLength)
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_TOO_LONG",
                    propertyPath,
                    $"'{property.Title}' must be at most {maxLength} character(s) long, but is {stringValue.Length}.");
            }
        }
        else if ((property.Minimum is not null || property.Maximum is not null) && TryToDecimal(value, out var numericValue))
        {
            if (property.Minimum is { } minimum && numericValue < minimum)
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_TOO_SMALL",
                    propertyPath,
                    $"'{property.Title}' must be at least {minimum}, but is {numericValue}.");
            }

            if (property.Maximum is { } maximum && numericValue > maximum)
            {
                yield return new ServiceBlueprintDiagnostic(
                    "COMPONENT_PROPERTY_TOO_LARGE",
                    propertyPath,
                    $"'{property.Title}' must be at most {maximum}, but is {numericValue}.");
            }
        }
    }

    // "Missing" for a Required check — distinct from merely "falsy" (false/0 are legitimate
    // values). An empty string matters here because InputComponent.FieldKey/Label etc. default
    // to "" rather than being C#-`required`, so a genuinely-missing value round-trips through
    // JSON as an empty string, not null — the C# type system alone can't catch it.
    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        ICollection c => c.Count == 0,
        _ => false,
    };

    private static bool TryToDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal d: result = d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case double db: result = (decimal)db; return true;
            case float f: result = (decimal)f; return true;
            default: result = 0; return false;
        }
    }

    // Path segments should address the JSON a diagnostic's reader is actually looking at
    // (ServiceBlueprintJson.WriteOptions uses JsonNamingPolicy.CamelCase), matching the same
    // convention ComponentExtensions.FlattenWithPaths already uses for component paths.
    private static string CamelCase(string propertyName) =>
        propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}
