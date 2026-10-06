using static Wayfinder.Models.ServiceDesign.Components.BuiltInComponentDescriptors;

namespace Wayfinder.Models.ServiceDesign.Components;

/// <summary>The text and date inputs, which can start on the visitor's own device clock.</summary>
internal static class DeviceClockDescriptors
{
    internal static ComponentDescriptor Text() =>
        new()
        {
            Discriminator = "text", DisplayName = "Text input", Category = ComponentCategory.Input,
            ClrType = typeof(TextInputComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(TextInputComponent.MinLength), "Minimum length", ComponentPropertyValueKind.Integer),
                Prop(nameof(TextInputComponent.MaxLength), "Maximum length", ComponentPropertyValueKind.Integer),
                Prop(nameof(TextInputComponent.Pattern), "Pattern (regex)", ComponentPropertyValueKind.String, format: "pattern"),
                Prop(nameof(TextInputComponent.Prefix), "Prefix", ComponentPropertyValueKind.String, "e.g. \"£\"."),
                Prop(nameof(TextInputComponent.DefaultToCurrentTime), "Default to the current time", ComponentPropertyValueKind.Boolean,
                    "Starts the field on the visitor's current time (24-hour HH:mm) from their own device clock. " +
                    "They can change it.", editor: "toggle"),
            ],
        };

    internal static ComponentDescriptor Date() =>
        new()
        {
            Discriminator = "date", DisplayName = "Date input", Category = ComponentCategory.Input,
            ClrType = typeof(DateInputComponent), IsInput = true,
            Properties =
            [
                .. InputBaseProperties(),
                Prop(nameof(DateInputComponent.DefaultToToday), "Default to today", ComponentPropertyValueKind.Boolean,
                    "Starts the field on today's date from the visitor's own device clock. They can change it.",
                    editor: "toggle"),
            ],
        };
}
