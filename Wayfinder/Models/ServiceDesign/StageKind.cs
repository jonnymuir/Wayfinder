using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// What a stage is for. It carries no runtime behaviour of its own — the step shell is inferred
/// from the stage's components — so it is an authoring label, and optional on a stage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StageKind>))]
public enum StageKind
{
    Question,
    CheckAnswers,
    Confirmation,
    TaskList,
}
