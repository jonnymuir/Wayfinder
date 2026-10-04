import { html } from 'lit';
import type { RouteView, ServiceBlueprint } from '../types.js';
import { serviceBlueprintGateways } from '../types.js';
import { deriveGatewayBindings } from '../gateway-representation.js';
import { stageQueueKey } from '../stage-assignment.js';

/** How the inspector names and describes the nodes a route joins. */

export function stageLabel(blueprint: ServiceBlueprint | null, stageKey: string): string {
  return (
    blueprint?.stages.find((stage) => stage.stageKey === stageKey)?.displayName ??
    serviceBlueprintGateways(blueprint).find((gateway) => gateway.key === stageKey)?.displayName ??
    stageKey
  );
}

export function gatewayLabel(blueprint: ServiceBlueprint | null, gatewayKey: string): string {
  return serviceBlueprintGateways(blueprint).find((gateway) => gateway.key === gatewayKey)?.displayName ?? gatewayKey;
}

/** "From → via gateway → To", with a spoken equivalent on the wrapping element. */
export function describeRoute(blueprint: ServiceBlueprint | null, transition: RouteView) {
  const fromStage = stageLabel(blueprint, transition.fromStage);
  const fromGateway = transition.fromGateway ? gatewayLabel(blueprint, transition.fromGateway) : null;
  const toGateway = transition.toGateway ? gatewayLabel(blueprint, transition.toGateway) : null;
  const toStage = stageLabel(blueprint, transition.toStage);

  const ariaParts = [`from ${fromStage}`];
  if (fromGateway) ariaParts.push(`via split gateway ${fromGateway}`);
  if (toGateway) ariaParts.push(`via join gateway ${toGateway}`);
  ariaParts.push(`to ${toStage}`);

  const visibleTokens = [fromStage, fromGateway, toGateway, toStage].filter((token): token is string => Boolean(token));
  const arrow = html`<span aria-hidden="true"> → </span>`;
  const visible = visibleTokens.map((token, index) => (index === 0 ? html`<span>${token}</span>` : html`${arrow}<span>${token}</span>`));

  return html`<span aria-label=${ariaParts.join(', ')}>${visible}</span>`;
}

/** The join gateways a route out of `stageKey` can arrive through: anchored to it, or unanchored in its queue. */
export function joinGatewaysAnchoredAt(blueprint: ServiceBlueprint | null, stageKey: string) {
  if (!blueprint) {
    return [];
  }

  const stage = blueprint.stages.find((candidate) => candidate.stageKey === stageKey);
  const queueKey = stage ? stageQueueKey(stage) : '';

  return deriveGatewayBindings(blueprint)
    .filter((binding) => binding.gateway.gatewayType === 'Join')
    .filter((binding) => binding.anchorStageKey === stageKey || (!binding.anchorStageKey && binding.queueKey === queueKey))
    .map((binding) => binding.gateway);
}
