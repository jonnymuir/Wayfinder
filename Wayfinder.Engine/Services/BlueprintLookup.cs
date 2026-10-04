using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>Pure lookups over a <see cref="ServiceBlueprint"/>: queues, gateways and a node's outgoing routes.</summary>
internal static class BlueprintLookup
{
    public static IReadOnlyList<QueueDefinition> GetQueues(ServiceBlueprint definition) =>
        definition.Queues ?? [];

    public static IReadOnlyList<ServiceBlueprintGatewayDefinition> GetGateways(ServiceBlueprint definition) =>
        definition.Gateways ?? [];

    public static ServiceBlueprintGatewayDefinition? FindGateway(ServiceBlueprint definition, string nodeKey) =>
        GetGateways(definition).FirstOrDefault(g =>
            string.Equals(g.Key, nodeKey, StringComparison.Ordinal));

    public static string? GetQueueKey(StageDefinition? stage) =>
        stage?.QueueKey;

    public static string? ResolveQueueName(ServiceBlueprint definition, string? queueKey)
    {
        if (string.IsNullOrWhiteSpace(queueKey))
        {
            return null;
        }

        var queue = GetQueues(definition).FirstOrDefault(candidate =>
            string.Equals(candidate.Key, queueKey, StringComparison.Ordinal));

        return queue?.Key ?? queueKey;
    }

    public static string? ResolveQueueName(ServiceBlueprint definition, StageDefinition? stage) =>
        stage is null
            ? null
            : ResolveQueueName(definition, GetQueueKey(stage));

    public static string? ResolveQueueName(ServiceBlueprint definition, ServiceBlueprintGatewayDefinition? gateway) =>
        gateway is null
            ? null
            : ResolveQueueName(definition, gateway.QueueKey);

    public static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// A route's trigger can be authored blank (an AI agent leaving it empty rather than omitting
    /// it, so it survives as "" not null) — default it to "continue" here, the single place raw
    /// route.Trigger values are read into a transition's Action, so the rendered button's value
    /// and the action-matching in <c>Advance</c> always agree on the same non-empty key.
    /// </summary>
    public static string ResolveTrigger(string? trigger) =>
        string.IsNullOrWhiteSpace(trigger) ? "continue" : trigger;

    /// <summary>The routes leaving <paramref name="sourceKey"/> (a stage, else a gateway), ordered by target then trigger.</summary>
    public static IReadOnlyList<RouteFile> GetOutgoingTransitions(ServiceBlueprint definition, string sourceKey)
    {
        var stage = definition.Stages.FirstOrDefault(candidate =>
            string.Equals(candidate.StageKey, sourceKey, StringComparison.Ordinal));
        if (stage?.Routes is { Count: > 0 })
        {
            return ToTransitions(sourceKey, stage.Routes);
        }

        var gateway = FindGateway(definition, sourceKey);
        return gateway?.Routes is { Count: > 0 }
            ? ToTransitions(gateway.Key, gateway.Routes)
            : [];
    }

    private static RouteFile[] ToTransitions(string fromKey, IEnumerable<ServiceBlueprintRouteDefinition> routes) =>
        routes
            .Select(route => new RouteFile
            {
                FromState = fromKey,
                ToState = route.Target,
                Action = ResolveTrigger(route.Trigger),
                Label = route.Label,
                Style = route.Style,
                RequiresRole = route.RequiresRole,
                ShowWhen = route.ShowWhen,
                Actions = route.Actions
            })
            .OrderBy(transition => transition.ToState, StringComparer.Ordinal)
            .ThenBy(transition => transition.Action, StringComparer.Ordinal)
            .ToArray();
}
