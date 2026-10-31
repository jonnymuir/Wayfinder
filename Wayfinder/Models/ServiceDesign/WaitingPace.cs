using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// When the answer a join gateway waits for usually comes back. It decides what the person waiting is told,
/// so a wait on a person or a slow process never claims to be "taking longer than usual" after a few minutes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WaitingPace>))]
public enum WaitingPace
{
    /// <summary>
    /// Seconds or minutes, usually an automated check. The page shows it is still working, says how long it
    /// usually takes, and says so when it is taking longer than usual.
    /// </summary>
    WhileTheyWait,

    /// <summary>
    /// Hours or days, usually a person or a slow process. The page says the person can leave and come back,
    /// and never says how long it should take.
    /// </summary>
    Later,
}
