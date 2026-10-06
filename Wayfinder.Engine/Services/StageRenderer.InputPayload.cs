using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Engine.Services;

internal sealed partial class StageRenderer
{
    private static IReadOnlyList<string>? OptionsOf(InputComponent input) => input switch
    {
        SelectComponent select => select.Options,
        RadiosComponent radios => radios.Options,
        CheckboxesComponent checkboxes => checkboxes.Options,
        GuidanceChecklistComponent guidance => guidance.Items.Select(i => i.Key).ToList(),
        _ => null,
    };

    private static string? DeviceDefaultOf(InputComponent input) => input switch
    {
        DateInputComponent { DefaultToToday: true } => "today",
        TextInputComponent { DefaultToCurrentTime: true } => "time",
        _ => null,
    };
}
