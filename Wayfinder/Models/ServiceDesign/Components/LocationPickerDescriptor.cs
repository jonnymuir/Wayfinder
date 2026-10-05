namespace Wayfinder.Models.ServiceDesign.Components;

internal static class LocationPickerDescriptor
{
    internal static ComponentDescriptor Build() => new()
    {
        Discriminator = "location-picker", DisplayName = "Location picker", Category = ComponentCategory.Input,
        Description = "A single geographic point, stored as \"latitude,longitude\" in WGS84 decimal degrees. " +
            "A text field is always the working control; a map and a current-location button enhance it.",
        ClrType = typeof(LocationPickerComponent), IsInput = true,
        Properties = BuiltInComponentDescriptors.InputBaseProperties(),
    };
}
