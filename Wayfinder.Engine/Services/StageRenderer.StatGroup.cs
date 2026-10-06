using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Engine.Services;

internal sealed partial class StageRenderer
{
    private static ComponentRenderPayload StatGroupPayload(StatGroupComponent statGroup, Dictionary<string, object?> displayValues) => new()
    {
        Type = "stat-group",
        Title = statGroup.Title,
        Stats = statGroup.Items.Select(item => new StatItem
        {
            Label = item.Label,
            FieldKey = item.FieldKey,
            Value = displayValues.TryGetValue(item.FieldKey, out var statValue) ? statValue?.ToString() : null,
            Qualifier = item.Qualifier,
            Emphasis = item.Emphasis,
            Display = item.Display,
            Width = item.Width,
        }).ToArray(),
    };
}
