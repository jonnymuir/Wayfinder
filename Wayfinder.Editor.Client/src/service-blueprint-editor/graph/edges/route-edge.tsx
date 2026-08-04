import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, Position, type EdgeProps } from '@xyflow/react';
import { useGraphCallbacks } from '../graph-callbacks.js';
import type { RouteFlowEdge, TransitionChip } from '../graph-model.js';

function chipClassName(chip: TransitionChip): string {
  return [
    'edge-chip',
    chip.branch ? 'branch-path' : '',
    chip.merge ? 'merge-path' : '',
    chip.selected ? 'selected' : '',
    chip.simulationPath ? 'simulation-path' : '',
  ].filter(Boolean).join(' ');
}

export function RouteEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  data,
}: EdgeProps<RouteFlowEdge>) {
  const callbacks = useGraphCallbacks();
  if (!data) {
    return null;
  }
  const { edge, fromKey, toKey, simulationPath, chips, readOnly } = data;

  const [path] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
    borderRadius: 6,
  });

  // When one route carries several transitions to the same target (e.g. approve/reject),
  // they'd otherwise draw as one shared line with only the labels stacked apart. Bend each
  // transition's own rail a little to either side of the shared line so the branches read as
  // distinct paths; the offset axis is whichever axis is perpendicular to the dominant
  // source→target direction, so a bend never runs parallel to the flow itself.
  const verticalFlow = (sourcePosition === Position.Top || sourcePosition === Position.Bottom)
    && (targetPosition === Position.Top || targetPosition === Position.Bottom);
  const pathForChip = (chip: TransitionChip): string => {
    if (chips.length <= 1 || !chip.railOffset) {
      return path;
    }
    const [chipPath] = getSmoothStepPath({
      sourceX,
      sourceY,
      sourcePosition,
      targetX,
      targetY,
      targetPosition,
      borderRadius: 6,
      ...(verticalFlow
        ? { centerX: (sourceX + targetX) / 2 + chip.railOffset }
        : { centerY: (sourceY + targetY) / 2 + chip.railOffset }),
    });
    return chipPath;
  };

  const basePathClass = [
    'edge-path',
    'route-rail',
    edge.backward ? 'loop-back' : '',
    simulationPath ? 'simulation-path' : '',
  ].filter(Boolean).join(' ');

  const handleChipKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>, chip: TransitionChip) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      callbacks.selectTransition(chip.index);
      return;
    }
    if (event.key.toLowerCase() === 'e') {
      event.preventDefault();
      callbacks.selectTransition(chip.index, { openInspector: true });
      return;
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      callbacks.requestDeleteTransition(chip.index);
      return;
    }
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault();
      const rect = event.currentTarget.getBoundingClientRect();
      callbacks.openContextMenu(
        { clientX: rect.left + rect.width / 2, clientY: rect.bottom },
        { kind: 'transition', transitionIndex: chip.index },
        event.currentTarget
      );
    }
  };

  const handleChipContextMenu = (event: React.MouseEvent<HTMLButtonElement>, chip: TransitionChip) => {
    if (readOnly) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    callbacks.openContextMenu(
      { clientX: event.clientX, clientY: event.clientY },
      { kind: 'transition', transitionIndex: chip.index },
      event.currentTarget
    );
  };

  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        markerEnd={markerEnd}
        className={basePathClass}
        data-wayfinder-route-path={edge.key}
        data-wayfinder-route-from={fromKey}
        data-wayfinder-route-to={toKey}
        data-wayfinder-route-simulation-path={String(simulationPath)}
      />
      {chips.map(chip => (
        <path
          key={`transition-path-${chip.index}`}
          d={pathForChip(chip)}
          fill="none"
          className={[
            'edge-path',
            'transition-overlay',
            chip.branch ? 'branch-path' : '',
            chip.merge ? 'merge-path' : '',
            chip.selected ? 'selected' : '',
            chip.simulationPath ? 'simulation-path' : '',
          ].filter(Boolean).join(' ')}
          data-wayfinder-transition-path={String(chip.index)}
          data-wayfinder-transition-from={chip.fromKey}
          data-wayfinder-transition-to={chip.toKey}
          data-wayfinder-transition-simulation-path={String(chip.simulationPath)}
        />
      ))}
      <EdgeLabelRenderer>
        {chips.map(chip => (
          <button
            key={`chip-${chip.index}`}
            type="button"
            className={chipClassName(chip)}
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${chip.x}px, ${chip.y}px)`,
              pointerEvents: 'all',
            }}
            aria-label={chip.ariaLabel}
            data-wayfinder-transition={String(chip.index)}
            data-wayfinder-transition-from={chip.fromKey}
            data-wayfinder-transition-to={chip.toKey}
            data-wayfinder-transition-simulation-path={String(chip.simulationPath)}
            onClick={() => callbacks.selectTransition(chip.index)}
            onDoubleClick={() => callbacks.selectTransition(chip.index, { openInspector: true })}
            onKeyDown={event => handleChipKeyDown(event, chip)}
            onContextMenu={event => handleChipContextMenu(event, chip)}
          >
            {chip.label}
          </button>
        ))}
      </EdgeLabelRenderer>
    </>
  );
}
