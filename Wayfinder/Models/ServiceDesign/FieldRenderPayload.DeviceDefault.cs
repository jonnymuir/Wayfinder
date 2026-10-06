namespace Wayfinder.Models.ServiceDesign;

public partial record FieldRenderPayload
{
    /// <summary>
    /// <c>"today"</c> (a date input) or <c>"time"</c> (a text input) when the field should start on the
    /// visitor's own device clock if it has no value; null otherwise. The device's clock is the only one
    /// that knows the visitor's local day and time, so a script fills it in rather than the server.
    /// </summary>
    public string? DeviceDefault { get; init; }
}
