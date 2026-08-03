import type { NodeProps } from '@xyflow/react';
import { useGraphCallbacks } from '../graph-callbacks.js';
import type { GatewayFlowNode } from '../graph-model.js';
import { iconForGateway } from '../node-icons.js';
import { HandleFan } from './handle-fan.js';
import { NodeIcon } from './node-icon.js';

export function GatewayNode({ data }: NodeProps<GatewayFlowNode>) {
  const callbacks = useGraphCallbacks();
  const {
    node, rowRank, sourceHandles, targetHandles, selected, routeCount, triggerLabel, conditionLabel, readOnly,
  } = data;
  const gateway = node.gateway;
  const isPill = node.pill;
  const shapeClass = isPill ? 'shape-pill' : 'shape-diamond';

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      callbacks.selectGateway(gateway.key);
      return;
    }
    if (event.key.toLowerCase() === 'e') {
      event.preventDefault();
      callbacks.selectGateway(gateway.key, { openInspector: true });
      return;
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      callbacks.requestDeleteGateway(gateway.key, event.currentTarget);
      return;
    }
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault();
      const rect = event.currentTarget.getBoundingClientRect();
      callbacks.openContextMenu(
        { clientX: rect.left + rect.width / 2, clientY: rect.bottom },
        { kind: 'gateway', gatewayKey: gateway.key },
        event.currentTarget
      );
    }
  };

  const handleContextMenu = (event: React.MouseEvent<HTMLButtonElement>) => {
    if (readOnly) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    callbacks.openContextMenu(
      { clientX: event.clientX, clientY: event.clientY },
      { kind: 'gateway', gatewayKey: gateway.key },
      event.currentTarget
    );
  };

  const className = [
    'gateway-node',
    node.surface,
    `kind-${gateway.gatewayType.toLowerCase()}`,
    shapeClass,
    selected ? 'selected' : '',
  ].filter(Boolean).join(' ');

  return (
    <div
      className={`gateway-node-shell ${shapeClass}`}
      data-wayfinder-gateway-node={gateway.key}
      data-wayfinder-gateway-shape={isPill ? 'pill' : 'diamond'}
      data-wayfinder-row-rank={String(rowRank)}
    >
      <HandleFan handles={targetHandles} type="target" readOnly={readOnly} />
      <button
        type="button"
        className={className}
        aria-pressed={selected}
        aria-label={isPill
          ? `${gateway.displayName}, single-route gateway via “${triggerLabel}”, ${node.queueLabel} queue`
          : `${gateway.displayName}, ${gateway.gatewayType} gateway, ${node.queueLabel} queue`}
        data-wayfinder-gateway={gateway.key}
        data-wayfinder-gateway-kind={gateway.gatewayType}
        data-wayfinder-gateway-route-count={String(routeCount)}
        data-wayfinder-queue={node.queueKey}
        onClick={() => callbacks.selectGateway(gateway.key)}
        onDoubleClick={() => callbacks.selectGateway(gateway.key, { openInspector: true })}
        onKeyDown={handleKeyDown}
        onContextMenu={handleContextMenu}
      >
        {isPill
          ? (
            <>
              <span className="pill-trigger">{triggerLabel || gateway.displayName}</span>
              {conditionLabel
                ? <span className="pill-condition" aria-label="conditional route" title={conditionLabel}>•</span>
                : null}
            </>
          )
          : (
            <>
              <span className="node-header">
                <span className="node-icon-chip"><NodeIcon icon={iconForGateway(gateway)} /></span>
                <span className="node-meta">{gateway.gatewayType}</span>
              </span>
              <span className="node-label">{gateway.displayName}</span>
            </>
          )}
      </button>
      <HandleFan handles={sourceHandles} type="source" readOnly={readOnly} />
    </div>
  );
}
