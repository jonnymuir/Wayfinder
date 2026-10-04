using System.Text.Json.Serialization;

namespace Wayfinder.Models.ServiceDesign;

/// <summary>A gateway either fans a request out into parallel cursors or merges them back.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GatewayKind>))]
public enum GatewayKind
{
    /// <summary>Fans out: every outgoing route starts a cursor.</summary>
    Split,

    /// <summary>Merges: waits for the required queues, then releases one outgoing route. Also the plain pass-through.</summary>
    Join,
}
