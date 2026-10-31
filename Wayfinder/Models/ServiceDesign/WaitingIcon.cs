using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>
/// The small, single-colour line icons a waiting page can show beside "Still working on it". Deliberately a short,
/// fixed list so every one stays quiet and professional; most waits need none.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WaitingIcon>))]
public enum WaitingIcon
{
    Hourglass,
    Search,
    Document,
    Butterfly,
    Hedgehog,
}
