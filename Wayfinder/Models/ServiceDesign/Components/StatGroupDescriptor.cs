using static Wayfinder.Models.ServiceDesign.Components.BuiltInComponentDescriptors;

namespace Wayfinder.Models.ServiceDesign.Components;

internal static class StatGroupDescriptor
{
    internal static ComponentDescriptor Build()
    {
        return new ComponentDescriptor
        {
            Discriminator = "stat-group", DisplayName = "Statistic group", Category = ComponentCategory.DataDisplay,
            Description = "A group of headline statistic tiles, resolved from instance/calculated field values.",
            ClrType = typeof(StatGroupComponent),
            Properties =
            [
                Prop(nameof(StatGroupComponent.Title), "Title", ComponentPropertyValueKind.String),
                new()
                {
                    Key = nameof(StatGroupComponent.Items), Title = "Statistic tiles",
                    ValueKind = ComponentPropertyValueKind.Array, Required = true,
                    Items = new ComponentPropertyDescriptor
                    {
                        Key = "item", Title = "Statistic tile", ValueKind = ComponentPropertyValueKind.Object,
                        Properties =
                        [
                            Prop(nameof(StatItemDefinition.Label), "Label", ComponentPropertyValueKind.String, required: true),
                            Prop(nameof(StatItemDefinition.FieldKey), "Field key", ComponentPropertyValueKind.String,
                                "The instance/calculated field this tile's value is read from. Must be a " +
                                "calculations.fields name or an input field's fieldKey captured anywhere in the " +
                                "blueprint (not just this stage).", format: "field-or-calc-ref", required: true),
                            Prop(nameof(StatItemDefinition.Qualifier), "Qualifier", ComponentPropertyValueKind.String, "e.g. \"a year, for life\"."),
                            Prop(nameof(StatItemDefinition.Emphasis), "Emphasis", ComponentPropertyValueKind.Boolean, editor: "toggle"),
                            Prop(nameof(StatItemDefinition.Display), "Display", ComponentPropertyValueKind.String,
                                "How the value is shown. \"map\" shows a read-only map when the value is a " +
                                "\"latitude,longitude\" point, such as a location-picker's; anything else is text.",
                                allowedValues: ["text", "map"]),
                        ],
                    },
                },
            ],
        };
    }
}
