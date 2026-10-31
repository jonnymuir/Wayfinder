namespace Wayfinder.Models.ServiceDesign;

public partial record ServiceBlueprint
{
    /// <summary>
    /// Warns about every join that makes someone wait without saying when the answer usually comes back. The
    /// page still works, but it can only be neutral: it cannot say how long is normal, or that the person can
    /// leave and come back.
    /// </summary>
    public IReadOnlyList<ServiceBlueprintDiagnostic> ValidateWaiting() =>
        (Gateways ?? [])
            .Where(gateway => gateway.GatewayType == GatewayKind.Join
                              && gateway.WaitingPace is null
                              && (gateway.RequiredIncomingQueues?.Count > 1 || gateway.WaitingContent is not null))
            .Select(gateway => new ServiceBlueprintDiagnostic(
                "GATEWAY_WAITING_PACE_UNSET",
                $"gateways.{gateway.Key}.waitingPace",
                $"Join gateway '{gateway.Key}' makes someone wait but does not say when the answer usually comes back. " +
                "Set waitingPace to WhileTheyWait (seconds or minutes, usually an automated check) or Later (hours " +
                "or days, usually a person or a slow process), so the waiting page can tell them what to expect.",
                ServiceBlueprintDiagnosticSeverity.Warning))
            .ToList();
}
