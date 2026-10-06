using Wayfinder.Models.ServiceDesign;
using Wayfinder.Rendering.GovUk;

namespace Wayfinder.Tests.Rendering;

/// <summary>A stat tile can ask for the whole row with <c>width: "full"</c>; otherwise it takes one grid cell.</summary>
public class StatGroupWidthTests
{
    private static string Render(string? width)
    {
        var step = new StepContent
        {
            StepType = "question",
            StateDisplayName = "Test",
            Components = [new ComponentRenderPayload { Type = "stat-group", Stats = [new StatItem { Label = "Habitat", FieldKey = "habitat", Value = "A long note", Width = width }] }],
            AvailableActions = [],
        };
        return new GovUkComponentRenderer().RenderForm(step, [], "/test", 0);
    }

    [Fact]
    public void AFullWidthTile_AsksForTheWholeRow() =>
        Assert.Contains("wayfinder-stat-card--full", Render("full"));

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    public void ATileWithoutAFullWidth_TakesOneCell(string? width) =>
        Assert.DoesNotContain("wayfinder-stat-card--full", Render(width));
}
